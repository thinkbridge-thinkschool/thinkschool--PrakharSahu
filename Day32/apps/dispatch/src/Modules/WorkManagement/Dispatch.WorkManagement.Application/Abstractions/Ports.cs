using Dispatch.WorkManagement.Domain.WorkOrders;

namespace Dispatch.WorkManagement.Application.Abstractions;

/// <summary>
/// How this module loads and stores work orders.
/// </summary>
/// <remarks>
/// <para>
/// A <b>port</b>: declared by the layer that needs it, implemented by the layer that knows how.
/// The interface lives in Application and the adapter lives in Infrastructure, which is what
/// makes the dependency point inwards — Infrastructure references Application, never the reverse.
/// Invert that one arrow and "clean architecture" is just three folders.
/// </para>
/// <para>
/// Note what is missing: no <c>IQueryable</c>, no <c>Update</c>, no <c>Include</c>. The interface
/// is deliberately narrow enough that a swap to a document store, or to two different stores for
/// reads and writes, is possible without touching a single use case. Expose <c>IQueryable</c> and
/// the ORM's semantics leak into every caller, at which point the port is decoration.
/// </para>
/// <para>
/// There is no <c>Update</c> because a loaded aggregate is already tracked by the unit of work.
/// An explicit update call invites the bug where somebody mutates an aggregate, forgets to call
/// it, and the change silently vanishes.
/// </para>
/// </remarks>
public interface IWorkOrderRepository
{
    Task<WorkOrder?> GetAsync(WorkOrderId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a work order for <b>reading only</b>. Changes to what comes back are not persisted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A separate method rather than a flag, because the distinction is not a detail the caller
    /// should be able to get wrong quietly. <see cref="GetAsync"/> returns a tracked aggregate
    /// that a use case is about to mutate; this returns a detached one for a query endpoint that
    /// will serialise it and forget it.
    /// </para>
    /// <para>
    /// The cost being avoided is real: EF builds a change-tracking entry for the aggregate root
    /// and for every owned labour row, then keeps them alive for the life of the context. On a
    /// read that is pure waste, and it grows with the number of labour entries -- so the busiest
    /// orders, the ones most likely to be looked at, pay the most.
    /// </para>
    /// <para>
    /// The danger of the alternative is worth stating: making <see cref="GetAsync"/> untracked
    /// would have been one word and would have silently broken every write, because EF would no
    /// longer notice the mutation. That is why this is a second method.
    /// </para>
    /// </remarks>
    Task<WorkOrder?> GetForReadAsync(WorkOrderId id, CancellationToken cancellationToken = default);

    Task AddAsync(WorkOrder workOrder, CancellationToken cancellationToken = default);

    /// <summary>Open orders past their SLA deadline. Used by the sweeper.</summary>
    /// <summary>
    /// Orders still waiting for Scheduling to answer, past their deadline.
    /// </summary>
    /// <remarks>
    /// Deliberately a separate query from <see cref="GetBreachingSlaAsync"/>, because the two ask
    /// different questions. An SLA breach means the work is late. This means the <em>saga</em> is
    /// stuck: we committed to a visit, asked Scheduling to hold a technician, and got no reply
    /// either way. An order can be stuck for minutes while its SLA has days left, so folding them
    /// together would hide this behind the looser deadline.
    /// </remarks>
    Task<IReadOnlyList<WorkOrder>> GetAwaitingReservationAsync(
        DateTimeOffset asOf, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkOrder>> GetBreachingSlaAsync(
        DateTimeOffset asAt, CancellationToken cancellationToken = default);
}
