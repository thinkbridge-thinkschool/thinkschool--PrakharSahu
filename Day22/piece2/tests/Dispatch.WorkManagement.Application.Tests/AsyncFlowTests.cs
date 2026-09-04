using Dispatch.Billing.Application.Invoices;
using Dispatch.Billing.Contracts;
using Dispatch.Billing.Infrastructure.Persistence;
using Dispatch.Scheduling.Application.Reservations;
using Dispatch.Scheduling.Contracts;
using Dispatch.Scheduling.Infrastructure.Persistence;
using Dispatch.WorkManagement.Application.WorkOrders;
using Dispatch.WorkManagement.Contracts;
using Dispatch.WorkManagement.Domain.WorkOrders;
using Dispatch.WorkManagement.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dispatch.WorkManagement.Application.Tests;

/// <summary>
/// The three async flows, driven end to end across real module boundaries.
/// </summary>
/// <remarks>
/// Every participant here is the production class. The only substitutions are the transport
/// (<see cref="TestBus"/> instead of the host's reflection-based publisher) and the clock. No
/// module is mocked — which is the point: these tests prove the modules actually compose, not
/// that each one behaves correctly in isolation against a fake version of its neighbour.
/// </remarks>
public class AsyncFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Customer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Technician = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Wires all three modules together over one bus, exactly as the host does.</summary>
    private sealed class System
    {
        public System()
        {
            Clock = new TestClock(Now);
            Bus = new TestBus();

            WorkOrders = new WorkOrderService(
                new InMemoryWorkOrderStore(), new InMemoryUnitOfWork(), Bus, Clock);

            Reservations = new InMemoryReservationStore();
            Invoices = new InMemoryInvoiceStore();

            // WorkManagement -> Scheduling
            Bus.Subscribe(new WorkOrderScheduledHandler(
                Reservations, Bus, NullLogger<WorkOrderScheduledHandler>.Instance));
            Bus.Subscribe(new WorkOrderReleasedHandler(
                Reservations, NullLogger<WorkOrderReleasedHandler>.Instance));

            // Scheduling -> WorkManagement (the compensating half)
            Bus.Subscribe(new ReservationFailedHandler(
                WorkOrders, NullLogger<ReservationFailedHandler>.Instance));

            // WorkManagement -> Billing
            Bus.Subscribe(new WorkOrderCompletedHandler(
                Invoices, Bus, NullLogger<WorkOrderCompletedHandler>.Instance));
        }

        public TestClock Clock { get; }
        public TestBus Bus { get; }
        public WorkOrderService WorkOrders { get; }
        public InMemoryReservationStore Reservations { get; }
        public InMemoryInvoiceStore Invoices { get; }

        public async Task<Guid> RaiseAndTriageAsync()
        {
            var id = (await WorkOrders.RaiseAsync(new RaiseWorkOrderRequest(
                Customer, "Chiller unit is not holding temperature",
                "Unit 4, Example Industrial Estate", "Testville", "TV1 9ZZ"))).Value;

            await WorkOrders.TriageAsync(id, WorkOrderPriority.High);
            return id;
        }

        public Task<SharedKernel.Result> ScheduleAsync(Guid id, int hoursFromNow = 2) =>
            WorkOrders.ScheduleAsync(id, new ScheduleWorkOrderRequest(
                Technician, Now.AddHours(hoursFromNow), Now.AddHours(hoursFromNow + 2)));

        public async Task CompleteAsync(Guid id, int minutes)
        {
            Clock.Advance(TimeSpan.FromHours(2));
            await WorkOrders.StartAsync(id);
            await WorkOrders.LogLabourAsync(id, new LogLabourRequest(Technician, minutes, "fixed"));
            await WorkOrders.CompleteAsync(id);
        }
    }

    // ==========================================================================================
    // Flow 1 — scheduling, and the saga that compensates when it fails
    // ==========================================================================================

    [Fact]
    public async Task Scheduling_an_order_reserves_the_technicians_slot()
    {
        var system = new System();
        var id = await system.RaiseAndTriageAsync();

        await system.ScheduleAsync(id);

        // WorkManagement published intent; Scheduling turned it into a held slot and confirmed.
        Assert.Single(system.Bus.OfType<WorkOrderScheduledV1>());
        Assert.Single(system.Bus.OfType<TechnicianReservedV1>());

        var order = await system.WorkOrders.GetAsync(id);
        Assert.Equal(WorkOrderStatus.Scheduled, order!.Status);
    }

    [Fact]
    public async Task A_double_booked_technician_sends_the_order_back_to_triage()
    {
        var system = new System();

        var first = await system.RaiseAndTriageAsync();
        await system.ScheduleAsync(first);

        var second = await system.RaiseAndTriageAsync();
        await system.ScheduleAsync(second);   // same technician, same window

        // The whole saga, in one call:
        //   WorkManagement commits Scheduled and publishes WorkOrderScheduledV1
        //   Scheduling sees the clash and publishes TechnicianReservationFailedV1
        //   WorkManagement handles it and walks the order back to Triaged
        Assert.Single(system.Bus.OfType<TechnicianReservationFailedV1>());

        var order = await system.WorkOrders.GetAsync(second);
        Assert.Equal(WorkOrderStatus.Triaged, order!.Status);
        Assert.Null(order.AssignedTechnicianId);

        // And the first booking is untouched. A failed compensation that also released the
        // winner's slot would be worse than not compensating at all.
        var winner = await system.WorkOrders.GetAsync(first);
        Assert.Equal(WorkOrderStatus.Scheduled, winner!.Status);
    }

    [Fact]
    public async Task The_returned_order_can_be_rescheduled_into_a_free_window()
    {
        var system = new System();
        var first = await system.RaiseAndTriageAsync();
        await system.ScheduleAsync(first, hoursFromNow: 2);

        var second = await system.RaiseAndTriageAsync();
        await system.ScheduleAsync(second, hoursFromNow: 2);      // clashes, bounces back
        var result = await system.ScheduleAsync(second, hoursFromNow: 6);   // a clear slot

        // Returning to triage is a recoverable state, not a dead end -- which is the difference
        // between a compensating action and simply failing.
        Assert.True(result.IsSuccess);
        var order = await system.WorkOrders.GetAsync(second);
        Assert.Equal(WorkOrderStatus.Scheduled, order!.Status);
    }

    [Fact]
    public async Task Cancelling_a_scheduled_order_releases_the_slot_for_someone_else()
    {
        var system = new System();
        var first = await system.RaiseAndTriageAsync();
        await system.ScheduleAsync(first);

        await system.WorkOrders.CancelAsync(first, "customer resolved it themselves");

        var second = await system.RaiseAndTriageAsync();
        var result = await system.ScheduleAsync(second);   // same window as the cancelled one

        Assert.Single(system.Bus.OfType<WorkOrderReleasedV1>());
        Assert.True(result.IsSuccess);
        Assert.Empty(system.Bus.OfType<TechnicianReservationFailedV1>());
    }

    // ==========================================================================================
    // Flow 2 — completion drafts an invoice
    // ==========================================================================================

    [Fact]
    public async Task Completing_an_order_drafts_an_invoice_in_Billing()
    {
        var system = new System();
        var id = await system.RaiseAndTriageAsync();
        await system.ScheduleAsync(id);

        await system.CompleteAsync(id, minutes: 90);

        var invoices = await system.Invoices.ListAsync();
        var invoice = Assert.Single(invoices);

        // 90 minutes rounds up to 2 billable hours at 85/hour. The pricing rule lives in Billing
        // and nowhere else -- WorkManagement reported minutes and has no idea what they cost.
        Assert.Equal(170m, invoice.Total.Amount);
        Assert.Equal("GBP", invoice.Total.Currency);
        Assert.Single(system.Bus.OfType<InvoiceDraftedV1>());
    }

    [Fact]
    public async Task A_cancelled_order_is_never_invoiced()
    {
        var system = new System();
        var id = await system.RaiseAndTriageAsync();
        await system.ScheduleAsync(id);
        system.Clock.Advance(TimeSpan.FromHours(2));
        await system.WorkOrders.StartAsync(id);
        await system.WorkOrders.LogLabourAsync(id, new LogLabourRequest(Technician, 45, "diagnosed, parts needed"));

        await system.WorkOrders.CancelAsync(id, "parts are on back order");

        // Labour was logged and the job was abandoned. Billing never hears about it, because
        // WorkOrderCompletedV1 is the only thing that drafts an invoice -- there is no code path
        // where an unfinished job produces a bill.
        Assert.Empty(await system.Invoices.ListAsync());
        Assert.Empty(system.Bus.OfType<WorkOrderCompletedV1>());
    }

    // ==========================================================================================
    // At-least-once delivery — every handler has to survive a duplicate
    // ==========================================================================================

    [Fact]
    public async Task A_redelivered_completion_does_not_invoice_the_customer_twice()
    {
        var system = new System();
        var id = await system.RaiseAndTriageAsync();
        await system.ScheduleAsync(id);
        await system.CompleteAsync(id, minutes: 60);

        var completed = system.Bus.OfType<WorkOrderCompletedV1>().Single();
        await system.Bus.RedeliverAsync(completed);
        await system.Bus.RedeliverAsync(completed);

        // The most expensive duplicate this system can produce. Every transport worth using is
        // at-least-once, so this WILL happen -- on a retry, on a redeploy mid-dispatch, on a
        // network blip. The handler dedupes by looking for an existing invoice.
        Assert.Single(await system.Invoices.ListAsync());
    }

    [Fact]
    public async Task A_redelivered_schedule_does_not_double_book_or_falsely_fail()
    {
        var system = new System();
        var id = await system.RaiseAndTriageAsync();
        await system.ScheduleAsync(id);

        var scheduled = system.Bus.OfType<WorkOrderScheduledV1>().Single();
        await system.Bus.RedeliverAsync(scheduled);

        // Naive overlap checking would see the reservation this event already created, call it a
        // clash, and bounce a perfectly good order back to triage. The handler checks for its own
        // prior work first, so a duplicate is a no-op rather than a self-inflicted failure.
        Assert.Empty(system.Bus.OfType<TechnicianReservationFailedV1>());

        var order = await system.WorkOrders.GetAsync(id);
        Assert.Equal(WorkOrderStatus.Scheduled, order!.Status);
    }

    [Fact]
    public async Task A_redelivered_release_is_harmless()
    {
        var system = new System();
        var id = await system.RaiseAndTriageAsync();
        await system.ScheduleAsync(id);
        await system.WorkOrders.CancelAsync(id, "no longer needed");

        var released = system.Bus.OfType<WorkOrderReleasedV1>().Single();
        await system.Bus.RedeliverAsync(released);

        var reused = await system.RaiseAndTriageAsync();
        Assert.True((await system.ScheduleAsync(reused)).IsSuccess);
    }

    // ==========================================================================================
    // The module boundary itself
    // ==========================================================================================

    [Fact]
    public async Task Only_the_three_documented_events_ever_leave_WorkManagement()
    {
        var system = new System();
        var id = await system.RaiseAndTriageAsync();
        await system.ScheduleAsync(id);
        await system.CompleteAsync(id, minutes: 30);

        var fromWorkManagement = system.Bus.Published
            .Where(e => e.GetType().Namespace == "Dispatch.WorkManagement.Contracts")
            .Select(e => e.GetType().Name)
            .Distinct()
            .Order()
            .ToArray();

        // Seven domain events were raised inside the module during that run. Three crossed the
        // boundary. The translation in WorkOrderService.PublishAsync is deliberately lossy, and
        // that loss is what lets WorkManagement rename or restructure its internal events without
        // asking anybody's permission.
        Assert.Equal(["WorkOrderCompletedV1", "WorkOrderScheduledV1"], fromWorkManagement);
    }

    [Fact]
    public async Task Domain_events_are_cleared_once_translated_so_they_cannot_be_published_twice()
    {
        var system = new System();
        var id = await system.RaiseAndTriageAsync();
        await system.ScheduleAsync(id);

        var order = await system.WorkOrders.GetAsync(id);

        // Left uncleared, the next mutation would re-translate and re-publish everything the
        // aggregate had ever done -- a duplicate storm that grows with the entity's age.
        Assert.Empty(order!.DomainEvents);
    }
}
