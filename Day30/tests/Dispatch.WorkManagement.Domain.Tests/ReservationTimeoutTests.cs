using Dispatch.WorkManagement.Domain.WorkOrders;

namespace Dispatch.WorkManagement.Domain.Tests;

/// <summary>
/// The saga's third outcome: no reply at all.
/// </summary>
/// <remarks>
/// Scheduling answers with a confirmation or a refusal, and both had handlers. Nothing covered
/// silence — a dropped event or a crashed consumer left the order in <c>Scheduled</c> for ever,
/// having already told a customer somebody was coming. These tests pin the aggregate half of the
/// fix: a deadline recorded at schedule time, a confirmation that clears it, and a predicate the
/// sweeper can ask.
/// </remarks>
public class ReservationTimeoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly TechnicianId Technician = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static (WorkOrder Order, TestClock Clock) Scheduled()
    {
        var clock = new TestClock(Now);
        var order = WorkOrder.Raise(
            new CustomerId(Guid.NewGuid()),
            "Chiller unit is not holding temperature",
            ServiceAddress.Create("Unit 4", "Testville", "TV1 9ZZ").Value,
            clock).Value;

        order.Triage(WorkOrderPriority.High, clock);
        var window = ScheduledWindow.Create(Now.AddHours(2), Now.AddHours(4), clock.UtcNow).Value;
        order.Schedule(Technician, window, clock);

        return (order, clock);
    }

    [Fact]
    public void Scheduling_records_a_deadline_for_the_reply()
    {
        var (order, _) = Scheduled();

        Assert.Equal(Now + WorkOrder.ReservationGrace, order.ReservationDeadline);
        Assert.Null(order.ReservationConfirmedAt);
    }

    [Fact]
    public void An_order_inside_its_grace_period_is_not_yet_stuck()
    {
        var (order, clock) = Scheduled();

        clock.Advance(WorkOrder.ReservationGrace - TimeSpan.FromSeconds(1));

        Assert.False(order.IsAwaitingReservation(clock.UtcNow));
    }

    [Fact]
    public void An_order_past_its_grace_period_with_no_reply_is_stuck()
    {
        var (order, clock) = Scheduled();

        clock.Advance(WorkOrder.ReservationGrace + TimeSpan.FromSeconds(1));

        Assert.True(order.IsAwaitingReservation(clock.UtcNow));
    }

    [Fact]
    public void A_confirmed_order_is_never_stuck_however_long_it_waits()
    {
        var (order, clock) = Scheduled();

        Assert.True(order.ConfirmReservation(clock).IsSuccess);
        clock.Advance(TimeSpan.FromDays(7));

        Assert.False(order.IsAwaitingReservation(clock.UtcNow));
        Assert.Equal(Now, order.ReservationConfirmedAt);
    }

    [Fact]
    public void Confirming_twice_is_a_no_op_rather_than_an_error()
    {
        // At-least-once delivery guarantees this happens. The second confirmation must not move
        // the recorded time, or the audit trail would say the booking was confirmed later than
        // it was.
        var (order, clock) = Scheduled();

        Assert.True(order.ConfirmReservation(clock).IsSuccess);
        var first = order.ReservationConfirmedAt;

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(order.ConfirmReservation(clock).IsSuccess);

        Assert.Equal(first, order.ReservationConfirmedAt);
    }

    [Fact]
    public void A_confirmation_arriving_after_work_started_is_still_accepted()
    {
        // Under a broker the reply is asynchronous, so a dispatcher can press "start" in the gap.
        // Refusing the confirmation then would throw away true information to satisfy a state
        // machine -- and would leave the order looking permanently unconfirmed.
        var (order, clock) = Scheduled();

        clock.Advance(TimeSpan.FromHours(2));
        Assert.True(order.Start(clock).IsSuccess);

        Assert.True(order.ConfirmReservation(clock).IsSuccess);
        Assert.NotNull(order.ReservationConfirmedAt);
    }

    [Fact]
    public void A_confirmation_for_a_cancelled_order_is_refused()
    {
        // The other side of the same coin: Scheduling is holding a slot for an order that no
        // longer wants it. Silently accepting would record a confirmation on a dead order; the
        // handler logs this at Warning because it means a technician may still be booked.
        var (order, clock) = Scheduled();

        Assert.True(order.Cancel("customer called back and cancelled").IsSuccess);

        var result = order.ConfirmReservation(clock);

        Assert.True(result.IsFailure);
        Assert.Null(order.ReservationConfirmedAt);
    }

    [Fact]
    public void Returning_to_triage_after_a_timeout_clears_the_booking()
    {
        var (order, clock) = Scheduled();
        clock.Advance(WorkOrder.ReservationGrace + TimeSpan.FromSeconds(1));

        Assert.True(order.ReturnToTriage("scheduling did not confirm the booking in time").IsSuccess);

        Assert.Equal(WorkOrderStatus.Triaged, order.Status);
        Assert.Null(order.AssignedTechnicianId);
        Assert.Null(order.Window);
        Assert.False(order.IsAwaitingReservation(clock.UtcNow));
    }

    [Fact]
    public void Rescheduling_after_a_timeout_starts_a_fresh_deadline()
    {
        // The stale deadline from the abandoned attempt must not make the new booking look
        // instantly stuck.
        var (order, clock) = Scheduled();
        clock.Advance(WorkOrder.ReservationGrace + TimeSpan.FromSeconds(1));
        order.ReturnToTriage("scheduling did not confirm the booking in time");

        var window = ScheduledWindow.Create(
            clock.UtcNow.AddHours(2), clock.UtcNow.AddHours(4), clock.UtcNow).Value;
        Assert.True(order.Schedule(Technician, window, clock).IsSuccess);

        Assert.Equal(clock.UtcNow + WorkOrder.ReservationGrace, order.ReservationDeadline);
        Assert.False(order.IsAwaitingReservation(clock.UtcNow));
    }
}
