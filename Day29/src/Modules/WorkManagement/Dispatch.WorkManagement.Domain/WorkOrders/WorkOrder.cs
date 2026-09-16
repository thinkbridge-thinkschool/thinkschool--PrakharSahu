using Dispatch.SharedKernel;

namespace Dispatch.WorkManagement.Domain.WorkOrders;

/// <summary>
/// The core aggregate: a request for field work, from report to completion.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is inside the boundary.</b> The order's own state, its scheduled window, and its
/// labour entries. Those are the things that must be consistent <em>at every commit</em>: the
/// rule "you cannot complete with no labour logged" is only enforceable without a query because
/// the entries live here.
/// </para>
/// <para>
/// <b>What is deliberately outside.</b> The technician, the customer, and the invoice — each held
/// by id only.
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Technician</b> belongs to Scheduling. Pulling it inside would mean loading a technician
///     to save a work order, locking that technician's row against every other order being saved
///     at the same moment. A busy technician would become the system's hottest write lock, for a
///     rule that is not even about them.
///   </item>
///   <item>
///     <b>Customer</b> belongs to a CRM context this module does not own. It is referenced, never
///     validated here — this module has no business deciding whether a customer is in good
///     standing.
///   </item>
///   <item>
///     <b>Invoice</b> belongs to Billing, and is created <em>after</em> completion, by a
///     subscriber. A work order that could not be completed because the invoicing service was
///     down would be an absurd coupling of a field engineer's day to an accounting system.
///   </item>
/// </list>
/// <para>
/// <b>Why every mutation returns <see cref="Result"/>.</b> Every method below can be called from
/// a stale UI — a dispatcher clicking "start" on an order somebody else already cancelled. That
/// is normal traffic, not an exceptional condition, so it is a return value rather than a thrown
/// exception. Exceptions here are reserved for genuine bugs.
/// </para>
/// <para>
/// <b>Why there are no public setters.</b> The only way to change a work order is to call a
/// method named after something that happens in the business. That is what keeps the invariants
/// enforceable: there is no path to a bad state that skips the rules, so "is this object valid?"
/// only has to be answered in one place per transition.
/// </para>
/// </remarks>
public sealed class WorkOrder : AggregateRoot<WorkOrderId>
{
    /// <summary>How long each priority is allowed before it breaches its SLA.</summary>
    /// <remarks>
    /// A table, not a chain of if-statements, so adding a priority is a one-line change that the
    /// compiler checks. It lives in the domain because "how urgent is urgent" is a business rule,
    /// not configuration — changing it changes what the company has promised its customers.
    /// </remarks>
    private static readonly Dictionary<WorkOrderPriority, TimeSpan> SlaTargets = new()
    {
        [WorkOrderPriority.Emergency] = TimeSpan.FromHours(4),
        [WorkOrderPriority.High] = TimeSpan.FromHours(24),
        [WorkOrderPriority.Standard] = TimeSpan.FromDays(3),
        [WorkOrderPriority.Low] = TimeSpan.FromDays(10)
    };

    private readonly List<LabourEntry> _labour = [];

    private WorkOrder(
        WorkOrderId id,
        CustomerId customerId,
        string summary,
        ServiceAddress address,
        DateTimeOffset raisedAt) : base(id)
    {
        CustomerId = customerId;
        Summary = summary;
        Address = address;
        RaisedAt = raisedAt;
        Status = WorkOrderStatus.Raised;
    }

    private WorkOrder() { }   // EF

    public CustomerId CustomerId { get; private set; }
    public string Summary { get; private set; } = string.Empty;
    public ServiceAddress Address { get; private set; } = null!;
    public WorkOrderStatus Status { get; private set; }

    public DateTimeOffset RaisedAt { get; private set; }
    public WorkOrderPriority? Priority { get; private set; }
    public DateTimeOffset? DueBy { get; private set; }

    public TechnicianId? AssignedTechnicianId { get; private set; }
    public ScheduledWindow? Window { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? CancellationReason { get; private set; }

    public IReadOnlyList<LabourEntry> Labour => _labour;
    public int TotalLabourMinutes => _labour.Sum(entry => entry.Minutes);

    /// <summary>
    /// True once the work is finished and the order can be invoiced.
    /// </summary>
    /// <remarks>
    /// Billing does not read this. It reacts to the integration event raised on completion —
    /// asking this module a question synchronously would put an accounting system on the critical
    /// path of a field engineer pressing "done" on a phone with two bars of signal.
    /// </remarks>
    public bool IsBillable => Status == WorkOrderStatus.Completed;

    // ------------------------------------------------------------------------------------------
    // Creation
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Reports a new problem. The only way a work order comes into existence.
    /// </summary>
    /// <remarks>
    /// A static factory rather than a public constructor, because construction can fail and a
    /// constructor's only way to say so is to throw. This returns the failure instead, and the
    /// private constructor means there is no second route in that skips the check.
    /// </remarks>
    public static Result<WorkOrder> Raise(
        CustomerId customerId,
        string? summary,
        ServiceAddress address,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(clock);

        if (string.IsNullOrWhiteSpace(summary))
        {
            return Result.Failure<WorkOrder>(WorkOrderErrors.SummaryRequired);
        }

        var order = new WorkOrder(WorkOrderId.New(), customerId, summary.Trim(), address, clock.UtcNow);
        order.Raise(new WorkOrderRaised(order.Id, customerId, order.Summary));

        return order;
    }

    // ------------------------------------------------------------------------------------------
    // Transitions
    // ------------------------------------------------------------------------------------------

    /// <summary>Assesses the order: sets a priority, and derives the SLA due date from it.</summary>
    /// <remarks>
    /// The due date is <em>computed here, once</em>, rather than being passed in or recalculated
    /// on read. Recalculating on read would silently move every existing order's deadline the day
    /// the SLA table changes — including orders that have already breached.
    /// </remarks>
    public Result Triage(WorkOrderPriority priority, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status != WorkOrderStatus.Raised)
        {
            return Result.Failure(WorkOrderErrors.WrongStatus("triage", Status, WorkOrderStatus.Raised));
        }

        Priority = priority;
        DueBy = RaisedAt + SlaTargets[priority];
        Status = WorkOrderStatus.Triaged;

        Raise(new WorkOrderTriaged(Id, priority, DueBy.Value));
        return Result.Success();
    }

    /// <summary>Commits to a technician and a time window.</summary>
    /// <remarks>
    /// This records the <em>intent</em>. It does not reserve the technician's calendar — that
    /// happens in the Scheduling module, in a different transaction, in response to the event
    /// this raises. See <see cref="ReturnToTriage"/> for what happens when that reservation
    /// fails.
    /// </remarks>
    public Result Schedule(TechnicianId technicianId, ScheduledWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (Status != WorkOrderStatus.Triaged)
        {
            return Result.Failure(WorkOrderErrors.WrongStatus("schedule", Status, WorkOrderStatus.Triaged));
        }

        AssignedTechnicianId = technicianId;
        Window = window;
        Status = WorkOrderStatus.Scheduled;

        Raise(new WorkOrderScheduled(Id, technicianId, window));
        return Result.Success();
    }

    /// <summary>
    /// Undoes a schedule that Scheduling could not honour.
    /// </summary>
    /// <remarks>
    /// The compensating action for a cross-module flow that has no shared transaction. It is a
    /// first-class domain operation rather than a quiet field reset, because "we told you we were
    /// coming and now we are not" is a real business event that dispatchers and customers need to
    /// hear about.
    /// </remarks>
    public Result ReturnToTriage(string reason)
    {
        if (Status != WorkOrderStatus.Scheduled)
        {
            return Result.Failure(WorkOrderErrors.WrongStatus("un-schedule", Status, WorkOrderStatus.Scheduled));
        }

        AssignedTechnicianId = null;
        Window = null;
        Status = WorkOrderStatus.Triaged;

        Raise(new WorkOrderReturnedToTriage(Id, string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim()));
        return Result.Success();
    }

    /// <summary>Marks work as begun on site.</summary>
    public Result Start(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status != WorkOrderStatus.Scheduled)
        {
            return Result.Failure(WorkOrderErrors.WrongStatus("start", Status, WorkOrderStatus.Scheduled));
        }

        // Starting before the promised window has opened is nearly always a mis-tap on the wrong
        // job in a list. Rejecting it protects the arrival-time data that the SLA report and the
        // customer's expectations both depend on.
        if (!Window!.HasOpenedBy(clock.UtcNow))
        {
            return Result.Failure(WorkOrderErrors.NotYetOpen);
        }

        StartedAt = clock.UtcNow;
        Status = WorkOrderStatus.InProgress;

        Raise(new WorkOrderStarted(Id, AssignedTechnicianId!.Value, StartedAt.Value));
        return Result.Success();
    }

    /// <summary>Records time spent. Only while the job is actually running.</summary>
    /// <remarks>
    /// Restricted to <see cref="WorkOrderStatus.InProgress"/> so that labour cannot be added to a
    /// completed order. Completion emits a total that Billing turns into money; letting that
    /// total change afterwards would mean the invoice and the work order disagree, with no event
    /// to reconcile them.
    /// </remarks>
    public Result LogLabour(TechnicianId technicianId, int minutes, string? note)
    {
        if (Status != WorkOrderStatus.InProgress)
        {
            return Result.Failure(WorkOrderErrors.WrongStatus("log labour against", Status, WorkOrderStatus.InProgress));
        }

        var entry = LabourEntry.Create(technicianId, minutes, note);
        if (entry.IsFailure)
        {
            return Result.Failure(entry.Error);
        }

        _labour.Add(entry.Value);
        return Result.Success();
    }

    /// <summary>Finishes the job. Terminal, and the point at which the order becomes billable.</summary>
    public Result Complete(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status != WorkOrderStatus.InProgress)
        {
            return Result.Failure(WorkOrderErrors.WrongStatus("complete", Status, WorkOrderStatus.InProgress));
        }

        // The invariant this aggregate exists to protect. A completed order with no labour is
        // either an unbillable job or a forgotten timesheet, and both are worth catching at the
        // moment of completion rather than in a month-end reconciliation.
        if (_labour.Count == 0)
        {
            return Result.Failure(WorkOrderErrors.NoLabourLogged);
        }

        CompletedAt = clock.UtcNow;
        Status = WorkOrderStatus.Completed;

        var withinSla = DueBy is null || CompletedAt <= DueBy;

        Raise(new WorkOrderCompleted(Id, CustomerId, TotalLabourMinutes, withinSla, CompletedAt.Value));
        return Result.Success();
    }

    /// <summary>Abandons the order. Allowed from any state except <see cref="WorkOrderStatus.Completed"/>.</summary>
    /// <remarks>
    /// Completed is excluded because the completion event has already left the module — Billing
    /// may have raised an invoice against it. Reversing that is a credit note, which is Billing's
    /// decision to make, not a state change this aggregate can perform on its behalf.
    ///
    /// Cancelling an already-cancelled order is also refused, so the cancellation event fires
    /// exactly once and downstream subscribers do not need to dedupe on top of their own
    /// idempotency.
    /// </remarks>
    public Result Cancel(string reason)
    {
        if (Status == WorkOrderStatus.Completed)
        {
            return Result.Failure(WorkOrderErrors.AlreadyTerminal);
        }

        if (Status == WorkOrderStatus.Cancelled)
        {
            return Result.Failure(WorkOrderErrors.WrongStatus(
                "cancel", Status,
                WorkOrderStatus.Raised, WorkOrderStatus.Triaged,
                WorkOrderStatus.Scheduled, WorkOrderStatus.InProgress));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(WorkOrderErrors.CancellationReasonRequired);
        }

        CancellationReason = reason.Trim();
        Status = WorkOrderStatus.Cancelled;

        Raise(new WorkOrderCancelled(Id, CancellationReason));
        return Result.Success();
    }

    /// <summary>
    /// True when the SLA deadline has passed and the work is not yet done.
    /// </summary>
    /// <remarks>
    /// A query, not a state. Breach is a function of the clock, so storing it would mean a
    /// background job writing to every open order just so a boolean stays honest — and being
    /// wrong in between runs. The SLA sweeper uses this to decide who to warn; it never sets it.
    /// </remarks>
    public bool HasBreachedSla(DateTimeOffset now) =>
        DueBy is { } dueBy
        && now > dueBy
        && Status is not (WorkOrderStatus.Completed or WorkOrderStatus.Cancelled);
}
