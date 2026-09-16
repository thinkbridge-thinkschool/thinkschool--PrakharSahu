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
