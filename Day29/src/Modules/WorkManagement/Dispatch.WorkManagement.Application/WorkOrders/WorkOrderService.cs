using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Application.Abstractions;
using Dispatch.WorkManagement.Contracts;
using Dispatch.WorkManagement.Domain.WorkOrders;

namespace Dispatch.WorkManagement.Application.WorkOrders;

// ==============================================================================================
// Use cases. One method per thing a user can do.
//
// Every one of them has the same four steps, and that sameness is the point:
//
//   1. load the aggregate            (or build it)
//   2. call ONE method on it         -- the domain decides, this layer does not
//   3. save                          -- one transaction, one aggregate
//   4. publish what other modules need to know
//
// There is no business logic here. If a rule ever appears in this file -- an `if` about status,
// a date comparison, a "but only when" -- it has escaped the aggregate, and the aggregate has
// stopped being able to guarantee anything about itself. That is the failure mode this layout
// exists to make visible: application services that grow rules are how a domain model quietly
// becomes an anaemic bag of properties.
//
// No MediatR. At kickoff a mediator adds indirection without buying anything: there are no
// pipeline behaviours yet, and `WorkOrders.CompleteAsync(...)` is greppable in a way that
// `Send(new CompleteWorkOrderCommand(...))` is not. It can be introduced later, behind exactly
// this surface, the day cross-cutting concerns actually need somewhere to live.
// ==============================================================================================

public sealed record RaiseWorkOrderRequest(Guid CustomerId, string Summary, string Line, string City, string Postcode);
public sealed record ScheduleWorkOrderRequest(Guid TechnicianId, DateTimeOffset WindowStart, DateTimeOffset WindowEnd);
public sealed record LogLabourRequest(Guid TechnicianId, int Minutes, string? Note);

public sealed class WorkOrderService(
    IWorkOrderRepository repository,
    IUnitOfWork unitOfWork,
    IIntegrationEventPublisher publisher,
    IClock clock)
{
    private static readonly Error NotFound = new("work_order.not_found", "No such work order.");

    public async Task<Result<Guid>> RaiseAsync(RaiseWorkOrderRequest request, CancellationToken ct = default)
    {
        var address = ServiceAddress.Create(request.Line, request.City, request.Postcode);
        if (address.IsFailure)
        {
            return Result.Failure<Guid>(address.Error);
        }

        var order = WorkOrder.Raise(new CustomerId(request.CustomerId), request.Summary, address.Value, clock);
        if (order.IsFailure)
        {
            return Result.Failure<Guid>(order.Error);
        }

        await repository.AddAsync(order.Value, ct);
        await unitOfWork.SaveChangesAsync(ct);

        return order.Value.Id.Value;
    }

    public Task<Result> TriageAsync(Guid id, WorkOrderPriority priority, CancellationToken ct = default) =>
        MutateAsync(id, order => order.Triage(priority, clock), ct);

    public async Task<Result> ScheduleAsync(Guid id, ScheduleWorkOrderRequest request, CancellationToken ct = default)
    {
        var window = ScheduledWindow.Create(request.WindowStart, request.WindowEnd, clock.UtcNow);
        if (window.IsFailure)
        {
            return window;
        }

        return await MutateAsync(id, order => order.Schedule(new TechnicianId(request.TechnicianId), window.Value), ct);
    }

    public Task<Result> StartAsync(Guid id, CancellationToken ct = default) =>
        MutateAsync(id, order => order.Start(clock), ct);

    public Task<Result> LogLabourAsync(Guid id, LogLabourRequest request, CancellationToken ct = default) =>
        MutateAsync(id, order => order.LogLabour(new TechnicianId(request.TechnicianId), request.Minutes, request.Note), ct);

    public Task<Result> CompleteAsync(Guid id, CancellationToken ct = default) =>
        MutateAsync(id, order => order.Complete(clock), ct);

    public Task<Result> CancelAsync(Guid id, string reason, CancellationToken ct = default) =>
        MutateAsync(id, order => order.Cancel(reason), ct);

    /// <summary>
    /// Walks a work order back to Triaged after Scheduling refused the booking.
    /// </summary>
    /// <remarks>
    /// Internal to the module, reached only from the handler for
    /// <c>TechnicianReservationFailedV1</c>. It is not on the public API surface because no human
    /// initiates it — it is the system correcting itself.
    /// </remarks>
    public Task<Result> ReturnToTriageAsync(Guid id, string reason, CancellationToken ct = default) =>
        MutateAsync(id, order => order.ReturnToTriage(reason), ct);

    public async Task<WorkOrder?> GetAsync(Guid id, CancellationToken ct = default) =>
        await repository.GetAsync(new WorkOrderId(id), ct);

    // ------------------------------------------------------------------------------------------
    // The shape every mutation shares.
    // ------------------------------------------------------------------------------------------
    private async Task<Result> MutateAsync(Guid id, Func<WorkOrder, Result> change, CancellationToken ct)
    {
        var order = await repository.GetAsync(new WorkOrderId(id), ct);
        if (order is null)
        {
            return Result.Failure(NotFound);
        }

        var result = change(order);
        if (result.IsFailure)
        {
            // Nothing was saved and nothing is published. A refused transition leaves no trace,
            // which is what lets a stale UI retry harmlessly.
            return result;
        }

        await unitOfWork.SaveChangesAsync(ct);
        await PublishAsync(order, ct);

        return Result.Success();
    }

    /// <summary>
    /// Translates the module's internal events into its published contract. The module edge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is the boundary made literal. Domain events go in, integration events come
    /// out, and the mapping is explicit and lossy on purpose — seven internal events become
    /// three published ones. Anything not translated here cannot leave the module, which means
    /// WorkManagement can rename, split or delete an internal event without asking anybody.
    /// </para>
    /// <para>
    /// It runs <b>after</b> <c>SaveChangesAsync</c>. Publishing before the commit would announce
    /// a decision the transaction may still roll back.
    /// </para>
    /// <para>
    /// Known gap, and the first thing Day 23 should fix: publish-after-commit is two operations,
    /// not one. A crash between them loses the event silently. The transactional outbox from Day
    /// 20 is the fix — write the integration event as a row inside the same transaction and let
    /// a relay publish it — and it slots in here without any other file changing.
    /// </para>
    /// </remarks>
    private async Task PublishAsync(WorkOrder order, CancellationToken ct)
    {
        // Snapshot and clear BEFORE publishing, not after.
        //
        // This is not a micro-optimisation, it is a correctness fix, and a test found it. The
        // scheduling saga re-enters this method on the same aggregate instance:
        //
        //     Schedule() -> publish WorkOrderScheduledV1
        //                -> Scheduling sees a clash, publishes TechnicianReservationFailedV1
        //                -> WorkManagement's handler calls ReturnToTriageAsync on THIS order
        //                -> the aggregate raises another domain event
        //                -> ...into the list this loop is still iterating
        //
        // which threw "Collection was modified". Clearing first means the re-entrant call starts
        // from an empty list and publishes its own events through its own MutateAsync, and the
        // outer loop finishes on a private copy.
        //
        // Worth noticing WHY it happened: in-process dispatch makes a publish synchronous and
        // therefore re-entrant. A broker would not reproduce this -- the compensating event would
        // arrive in a later request, on a freshly loaded aggregate. That difference is the real
        // cost of an in-process bus, and it argues for keeping handlers re-entrancy-safe rather
        // than relying on a transport that happens to serialise them today.
        var pending = order.DomainEvents.ToArray();
        order.ClearDomainEvents();

        foreach (var domainEvent in pending)
        {
            IIntegrationEvent? outbound = domainEvent switch
            {
                WorkOrderScheduled e => new WorkOrderScheduledV1(
                    e.WorkOrderId.Value, e.TechnicianId.Value, e.Window.Start, e.Window.End),

                WorkOrderCompleted e => new WorkOrderCompletedV1(
                    e.WorkOrderId.Value, e.CustomerId.Value, e.TotalLabourMinutes, e.WithinSla, e.CompletedAt),

                // Cancellation and return-to-triage collapse into one published event: from the
                // outside, both mean "release the slot".
                WorkOrderCancelled e => new WorkOrderReleasedV1(e.WorkOrderId.Value, e.Reason),
                WorkOrderReturnedToTriage e => new WorkOrderReleasedV1(e.WorkOrderId.Value, e.Reason),

                // Raised, Triaged and Started stay inside. Nothing outside this module has a
                // reason to care, and publishing them would create subscribers we then owe
                // compatibility to forever.
                _ => null
            };

            if (outbound is not null)
            {
                await publisher.PublishAsync(outbound, ct);
            }
        }
    }
}
