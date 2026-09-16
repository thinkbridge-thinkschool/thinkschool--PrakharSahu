using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Domain.WorkOrders;

namespace Dispatch.WorkManagement.Domain.Tests;

/// <summary>A clock the test drives. The whole reason <see cref="IClock"/> is injected.</summary>
internal sealed class TestClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// The aggregate's invariants, one per test.
/// </summary>
/// <remarks>
/// Note what none of these tests need: a container, a database, a mock, a web host. A domain
/// model that requires any of those to be exercised has infrastructure mixed into it, and the
/// architecture tests exist to keep that true.
///
/// The setup helpers below are the entire test fixture.
/// </remarks>
public class WorkOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly CustomerId Customer = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly TechnicianId Technician = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static ServiceAddress Address() =>
        ServiceAddress.Create("Unit 4, Example Industrial Estate", "Testville", "tv1 9zz").Value;

    private static (WorkOrder Order, TestClock Clock) Raised()
    {
        var clock = new TestClock(Now);
        var order = WorkOrder.Raise(Customer, "Chiller unit is not holding temperature", Address(), clock).Value;
        return (order, clock);
    }

    private static (WorkOrder Order, TestClock Clock) Scheduled()
    {
        var (order, clock) = Raised();
        order.Triage(WorkOrderPriority.High, clock);
        var window = ScheduledWindow.Create(Now.AddHours(2), Now.AddHours(4), clock.UtcNow).Value;
        order.Schedule(Technician, window);
        return (order, clock);
    }

    private static (WorkOrder Order, TestClock Clock) InProgress()
    {
        var (order, clock) = Scheduled();
        clock.Advance(TimeSpan.FromHours(2));
        order.Start(clock);
        return (order, clock);
    }

    // ==========================================================================================
    // Creation
    // ==========================================================================================

    [Fact]
    public void A_raised_order_starts_in_Raised_with_no_priority()
    {
        var (order, _) = Raised();

        Assert.Equal(WorkOrderStatus.Raised, order.Status);
        Assert.Null(order.Priority);

        // No due date until triage. A deadline before anyone has assessed urgency would be a
        // number invented by the system and then reported to a customer as a promise.
        Assert.Null(order.DueBy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_order_cannot_be_raised_without_a_summary(string? summary)
    {
        var result = WorkOrder.Raise(Customer, summary, Address(), new TestClock(Now));

        Assert.True(result.IsFailure);
        Assert.Equal("work_order.summary_required", result.Error.Code);
    }

    [Fact]
    public void Raising_records_the_fact_that_it_happened()
    {
        var (order, _) = Raised();

        Assert.Single(order.DomainEvents);
        Assert.IsType<WorkOrderRaised>(order.DomainEvents[0]);
    }

    // ==========================================================================================
    // Triage and the SLA
    // ==========================================================================================

    [Theory]
    [InlineData(WorkOrderPriority.Emergency, 4)]
    [InlineData(WorkOrderPriority.High, 24)]
    [InlineData(WorkOrderPriority.Standard, 72)]
    [InlineData(WorkOrderPriority.Low, 240)]
    public void Triage_derives_the_due_date_from_the_priority(WorkOrderPriority priority, int expectedHours)
    {
        var (order, clock) = Raised();

        var result = order.Triage(priority, clock);

        Assert.True(result.IsSuccess);
        Assert.Equal(order.RaisedAt.AddHours(expectedHours), order.DueBy);
    }

    [Fact]
    public void The_due_date_runs_from_when_the_problem_was_reported_not_when_it_was_triaged()
    {
        var (order, clock) = Raised();

        clock.Advance(TimeSpan.FromHours(6));   // it sat in a queue over a weekend
        order.Triage(WorkOrderPriority.High, clock);

        // The customer's clock started when they called. Measuring from triage instead would let
        // the SLA be reset by the company's own slowness -- which is precisely the delay it is
        // supposed to be measuring.
        Assert.Equal(Now.AddHours(24), order.DueBy);
    }

    [Fact]
    public void An_order_cannot_be_triaged_twice()
    {
        var (order, clock) = Raised();
        order.Triage(WorkOrderPriority.Low, clock);

        var result = order.Triage(WorkOrderPriority.Emergency, clock);

        // Re-triage would silently move a deadline that has already been communicated. Changing
        // priority after the fact is a real business need, but it is a different operation with
        // its own rules, not a second call to this one.
        Assert.True(result.IsFailure);
        Assert.StartsWith("work_order.wrong_status", result.Error.Code);
        Assert.Equal(WorkOrderPriority.Low, order.Priority);
    }

    // ==========================================================================================
    // Scheduling
    // ==========================================================================================

    [Fact]
    public void An_untriaged_order_cannot_be_scheduled()
    {
        var (order, clock) = Raised();
        var window = ScheduledWindow.Create(Now.AddHours(2), Now.AddHours(4), clock.UtcNow).Value;

        var result = order.Schedule(Technician, window);

        // Sending someone before anyone has decided how urgent it is means the dispatch order is
        // whatever arrived first, not whatever matters most.
        Assert.True(result.IsFailure);
        Assert.Equal(WorkOrderStatus.Raised, order.Status);
    }

    [Fact]
    public void A_window_cannot_start_in_the_past()
    {
        var result = ScheduledWindow.Create(Now.AddHours(-1), Now.AddHours(1), Now);

        Assert.True(result.IsFailure);
        Assert.Equal("window.in_the_past", result.Error.Code);
    }

    [Fact]
    public void A_window_must_end_after_it_starts()
    {
        var result = ScheduledWindow.Create(Now.AddHours(4), Now.AddHours(2), Now);

        Assert.True(result.IsFailure);
        Assert.Equal("window.inverted", result.Error.Code);
    }

    [Fact]
    public void Returning_to_triage_releases_the_technician_and_the_window()
    {
        var (order, _) = Scheduled();

        var result = order.ReturnToTriage("the technician is already booked");

        // The compensating action for the scheduling saga. Leaving the technician assigned would
        // mean the order believes it has someone who was never actually booked -- the exact
        // inconsistency the compensation exists to remove.
        Assert.True(result.IsSuccess);
        Assert.Equal(WorkOrderStatus.Triaged, order.Status);
        Assert.Null(order.AssignedTechnicianId);
        Assert.Null(order.Window);

        // The priority and due date survive. Triage was correct; only the booking was not.
        Assert.Equal(WorkOrderPriority.High, order.Priority);
        Assert.NotNull(order.DueBy);
    }

    // ==========================================================================================
    // Starting
    // ==========================================================================================

    [Fact]
    public void Work_cannot_start_before_the_window_opens()
    {
        var (order, clock) = Scheduled();   // window opens at Now + 2h, clock is still at Now

        var result = order.Start(clock);

        Assert.True(result.IsFailure);
        Assert.Equal("work_order.window_not_open", result.Error.Code);
        Assert.Equal(WorkOrderStatus.Scheduled, order.Status);
    }

    [Fact]
    public void Work_starts_once_the_window_has_opened()
    {
        var (order, clock) = Scheduled();
        clock.Advance(TimeSpan.FromHours(2));

        var result = order.Start(clock);

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkOrderStatus.InProgress, order.Status);
        Assert.Equal(clock.UtcNow, order.StartedAt);
    }

    // ==========================================================================================
    // Labour and completion -- the invariant this aggregate exists for
    // ==========================================================================================

    [Fact]
    public void An_order_cannot_be_completed_with_no_labour_logged()
    {
        var (order, clock) = InProgress();

        var result = order.Complete(clock);

        // The rule the aggregate boundary was drawn around. It is answerable without a query
        // precisely because labour entries live inside the boundary; move them out and this
        // becomes a check that can race.
        Assert.True(result.IsFailure);
        Assert.Equal("work_order.no_labour", result.Error.Code);
        Assert.Equal(WorkOrderStatus.InProgress, order.Status);
    }

    [Fact]
    public void An_order_completes_once_labour_has_been_logged()
    {
        var (order, clock) = InProgress();
        order.LogLabour(Technician, 90, "replaced the compressor relay");

        var result = order.Complete(clock);

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkOrderStatus.Completed, order.Status);
        Assert.True(order.IsBillable);
        Assert.Equal(90, order.TotalLabourMinutes);
    }

    [Fact]
    public void Labour_cannot_be_logged_before_work_starts()
    {
        var (order, _) = Scheduled();

        var result = order.LogLabour(Technician, 30, "travel");

        Assert.True(result.IsFailure);
        Assert.StartsWith("work_order.wrong_status", result.Error.Code);
    }

    [Fact]
    public void Labour_cannot_be_added_after_completion()
    {
        var (order, clock) = InProgress();
        order.LogLabour(Technician, 60, "diagnostics");
        order.Complete(clock);

        var result = order.LogLabour(Technician, 30, "forgot this bit");

        // Completion published a labour total that Billing has already turned into money.
        // Letting the total move afterwards means the invoice and the work order disagree, with
        // no event to reconcile them.
        Assert.True(result.IsFailure);
        Assert.Equal(60, order.TotalLabourMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-15)]
    public void Labour_must_be_a_positive_number_of_minutes(int minutes)
    {
        var (order, _) = InProgress();

        var result = order.LogLabour(Technician, minutes, null);

        Assert.True(result.IsFailure);
        Assert.Equal("labour.not_positive", result.Error.Code);
    }

    [Fact]
    public void A_single_labour_entry_longer_than_a_day_is_rejected()
    {
        var (order, _) = InProgress();

        var result = order.LogLabour(Technician, 25 * 60, "very long day");

        Assert.True(result.IsFailure);
        Assert.Equal("labour.implausible", result.Error.Code);
    }

    [Fact]
    public void Completion_reports_whether_the_sla_was_met()
    {
        var (order, clock) = InProgress();
        order.LogLabour(Technician, 45, "reset the controller");

        clock.Advance(TimeSpan.FromDays(3));   // High priority allows 24 hours
        order.Complete(clock);

        var completed = Assert.IsType<WorkOrderCompleted>(
            order.DomainEvents.Single(e => e is WorkOrderCompleted));

        // Carried on the event rather than left for a reader to recompute. The comparison needs
        // the due date AND the completion time, and once the order is archived the second one is
        // the only place the answer still exists in the form the customer was promised.
        Assert.False(completed.WithinSla);
    }

    // ==========================================================================================
    // Cancellation
    // ==========================================================================================

    [Fact]
    public void A_completed_order_cannot_be_cancelled()
    {
        var (order, clock) = InProgress();
        order.LogLabour(Technician, 30, "done");
        order.Complete(clock);

        var result = order.Cancel("customer changed their mind");

        // Completion has already left the module -- Billing may have raised an invoice against
        // it. Undoing that is a credit note, which is Billing's decision, not a state change this
        // aggregate can make on its behalf.
        Assert.True(result.IsFailure);
        Assert.Equal("work_order.terminal", result.Error.Code);
        Assert.Equal(WorkOrderStatus.Completed, order.Status);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Raised)]
    [InlineData(WorkOrderStatus.Triaged)]
    [InlineData(WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.InProgress)]
    public void An_order_can_be_cancelled_from_any_non_terminal_state(WorkOrderStatus from)
    {
        var (order, clock) = Raised();

        if (from >= WorkOrderStatus.Triaged) order.Triage(WorkOrderPriority.Standard, clock);
        if (from >= WorkOrderStatus.Scheduled)
            order.Schedule(Technician, ScheduledWindow.Create(Now.AddHours(2), Now.AddHours(4), clock.UtcNow).Value);
        if (from >= WorkOrderStatus.InProgress) { clock.Advance(TimeSpan.FromHours(2)); order.Start(clock); }

        var result = order.Cancel("access to the site was refused");

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkOrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public void Cancelling_requires_a_reason()
    {
        var (order, _) = Raised();

        var result = order.Cancel("  ");

        Assert.True(result.IsFailure);
        Assert.Equal("work_order.cancellation_reason_required", result.Error.Code);
    }

    [Fact]
    public void An_order_cannot_be_cancelled_twice()
    {
        var (order, _) = Raised();
        order.Cancel("duplicate report");

        var result = order.Cancel("duplicate report");

        // So the cancellation event fires exactly once. Subscribers are idempotent anyway, but
        // they should not have to be for a duplicate the aggregate could have refused.
        Assert.True(result.IsFailure);
    }

    // ==========================================================================================
    // SLA breach -- a query, never stored state
    // ==========================================================================================

    [Fact]
    public void An_open_order_past_its_due_date_has_breached()
    {
        var (order, clock) = Raised();
        order.Triage(WorkOrderPriority.Emergency, clock);   // 4 hours

        Assert.False(order.HasBreachedSla(Now.AddHours(3)));
        Assert.True(order.HasBreachedSla(Now.AddHours(5)));
    }

    [Fact]
    public void A_finished_order_never_counts_as_breaching()
    {
        var (order, clock) = InProgress();
        order.LogLabour(Technician, 30, "done");
        order.Complete(clock);

        // It may well have been finished late -- WorkOrderCompleted.WithinSla records that. But
        // "breaching" means "still open and overdue", which is the set the sweeper needs to chase.
        // A completed order is nobody's outstanding problem.
        Assert.False(order.HasBreachedSla(Now.AddYears(1)));
    }

    [Fact]
    public void An_untriaged_order_cannot_breach_because_it_has_no_deadline()
    {
        var (order, _) = Raised();

        Assert.False(order.HasBreachedSla(Now.AddYears(1)));
    }
}
