using Dispatch.Scheduling.Application.Reservations;
using Dispatch.Scheduling.Contracts;
using Dispatch.WorkManagement.Application.WorkOrders;
using Dispatch.WorkManagement.Domain.WorkOrders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dispatch.WorkManagement.Application.Tests;

/// <summary>
/// The scheduling saga, across the module boundary, including the outcome nobody had modelled.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AsyncFlowTests"/> already covers confirm-and-refuse. These cover what the review
/// found missing: that a confirmation is actually <em>recorded</em>, and that silence is
/// eventually treated as failure rather than as a booking that simply never resolves.
/// </para>
/// <para>
/// The bus here is synchronous, so the reply always arrives immediately. That is the honest
/// limit of these tests and the reason the timeout is driven by advancing a clock rather than by
/// waiting: the condition being tested cannot occur in-process, which is exactly why it went
/// unnoticed.
/// </para>
/// </remarks>
public class SagaCompletionTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Customer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Technician = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class System
    {
        public System(bool schedulingAnswers = true)
        {
            Clock = new TestClock(Now);
            Bus = new TestBus();
            Repository = new FakeWorkOrderRepository();
            WorkOrders = new WorkOrderService(Repository, new FakeUnitOfWork(), Bus, Clock);
            Reservations = new FakeReservationRepository();

            if (schedulingAnswers)
            {
                Bus.Subscribe(new WorkOrderScheduledHandler(
                    Reservations, Bus, NullLogger<WorkOrderScheduledHandler>.Instance));
            }

            Bus.Subscribe(new WorkOrderReleasedHandler(
                Reservations, NullLogger<WorkOrderReleasedHandler>.Instance));
            Bus.Subscribe(new ReservationFailedHandler(
                WorkOrders, NullLogger<ReservationFailedHandler>.Instance));
            Bus.Subscribe(new ReservationConfirmedHandler(
                WorkOrders, NullLogger<ReservationConfirmedHandler>.Instance));
        }

        public TestClock Clock { get; }
        public TestBus Bus { get; }
        public FakeWorkOrderRepository Repository { get; }
        public WorkOrderService WorkOrders { get; }
        public FakeReservationRepository Reservations { get; }

        public async Task<Guid> ScheduledOrderAsync(int hoursFromNow = 2)
        {
            var id = (await WorkOrders.RaiseAsync(new RaiseWorkOrderRequest(
                Customer, "Chiller unit is not holding temperature",
                "Unit 4, Example Industrial Estate", "Testville", "TV1 9ZZ"))).Value;

            await WorkOrders.TriageAsync(id, WorkOrderPriority.High);
            await WorkOrders.ScheduleAsync(id, new ScheduleWorkOrderRequest(
                Technician, Now.AddHours(hoursFromNow), Now.AddHours(hoursFromNow + 2)));

            return id;
        }
    }

    [Fact]
    public async Task A_confirmed_booking_is_recorded_on_the_work_order()
    {
        var system = new System();

        var id = await system.ScheduledOrderAsync();
        var order = await system.WorkOrders.GetAsync(id);

        Assert.Equal(WorkOrderStatus.Scheduled, order!.Status);
        Assert.NotNull(order.ReservationConfirmedAt);
    }

    [Fact]
    public async Task A_refused_booking_leaves_no_confirmation_behind()
    {
        var system = new System();

        // First order takes the slot; the second collides and is sent back to triage.
        await system.ScheduledOrderAsync();
        var second = await system.ScheduledOrderAsync();

        var order = await system.WorkOrders.GetAsync(second);

        Assert.Equal(WorkOrderStatus.Triaged, order!.Status);
        Assert.Null(order.ReservationConfirmedAt);
    }

    [Fact]
    public async Task Scheduling_that_never_answers_leaves_the_order_stuck()
    {
        // The whole point. With no subscriber, WorkOrderScheduledV1 goes nowhere -- exactly what a
        // dropped message or a crashed consumer looks like from here.
        var system = new System(schedulingAnswers: false);

        var id = await system.ScheduledOrderAsync();
        var order = await system.WorkOrders.GetAsync(id);

        Assert.Equal(WorkOrderStatus.Scheduled, order!.Status);
        Assert.Null(order.ReservationConfirmedAt);

        // Before the grace period the order is simply waiting, which is correct.
        Assert.False(order.IsAwaitingReservation(system.Clock.UtcNow));

        system.Clock.Advance(WorkOrder.ReservationGrace + TimeSpan.FromSeconds(1));

        Assert.True(order.IsAwaitingReservation(system.Clock.UtcNow));
    }

    [Fact]
    public async Task The_sweeper_query_finds_only_the_orders_that_are_actually_stuck()
    {
        // One order whose booking was confirmed, one whose reply never came. Both are Scheduled,
        // both are past the grace period; only the second is stuck. Before the confirmation was
        // recorded these two were indistinguishable.
        var answering = new System();
        var confirmed = await answering.ScheduledOrderAsync();

        var silent = new System(schedulingAnswers: false);
        var stuck = await silent.ScheduledOrderAsync();

        answering.Clock.Advance(WorkOrder.ReservationGrace + TimeSpan.FromSeconds(1));
        silent.Clock.Advance(WorkOrder.ReservationGrace + TimeSpan.FromSeconds(1));

        var notStuck = await answering.Repository.GetAwaitingReservationAsync(answering.Clock.UtcNow);
        var isStuck = await silent.Repository.GetAwaitingReservationAsync(silent.Clock.UtcNow);

        Assert.Empty(notStuck);
        Assert.Single(isStuck);
        Assert.Equal(stuck, isStuck[0].Id.Value);
        Assert.NotEqual(confirmed, isStuck[0].Id.Value);
    }

    [Fact]
    public async Task Timing_out_frees_the_technician_for_somebody_else()
    {
        // The timeout is only useful if it undoes the booking. ReturnToTriage raises
        // WorkOrderReturnedToTriage, Scheduling releases the slot, and the window becomes
        // bookable again -- the same path an explicit refusal takes.
        var system = new System();
        var id = await system.ScheduledOrderAsync();

        system.Clock.Advance(WorkOrder.ReservationGrace + TimeSpan.FromSeconds(1));

        var result = await system.WorkOrders.ReturnToTriageAsync(
            id, "scheduling did not confirm the booking in time");
        Assert.True(result.IsSuccess);

        var reservation = await system.Reservations.GetByWorkOrderAsync(id);
        Assert.True(reservation!.IsReleased);

        // And the slot really is free: a different order can take the identical window.
        var next = await system.ScheduledOrderAsync();
        var taken = await system.WorkOrders.GetAsync(next);
        Assert.Equal(WorkOrderStatus.Scheduled, taken!.Status);
        Assert.NotNull(taken.ReservationConfirmedAt);
    }

    [Fact]
    public async Task A_late_confirmation_for_a_timed_out_order_is_refused_not_silently_applied()
    {
        // The nasty one. Under a broker the confirmation can arrive after the timeout already
        // gave up. Applying it would mark an order confirmed that has been returned to triage and
        // whose slot has been released -- a booking nobody is holding.
        var system = new System(schedulingAnswers: false);
        var id = await system.ScheduledOrderAsync();

        system.Clock.Advance(WorkOrder.ReservationGrace + TimeSpan.FromSeconds(1));
        await system.WorkOrders.ReturnToTriageAsync(id, "scheduling did not confirm the booking in time");

        // The reply finally turns up.
        await system.Bus.PublishAsync(new TechnicianReservedV1(
            id, Technician, Now.AddHours(2), Now.AddHours(4)));

        var order = await system.WorkOrders.GetAsync(id);

        Assert.Equal(WorkOrderStatus.Triaged, order!.Status);
        Assert.Null(order.ReservationConfirmedAt);
    }
}
