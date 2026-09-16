using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Application.Abstractions;
using Dispatch.WorkManagement.Domain.WorkOrders;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.WorkManagement.Infrastructure.Persistence;

/// <summary>
/// The work order repository, backed by EF Core.
/// </summary>
/// <remarks>
/// Replaces <c>InMemoryWorkOrderStore</c>. The port did not change, which is the point of having
/// declared it in the Application layer: the use cases and all 46 domain and application tests
/// are untouched by today's work.
/// </remarks>
public sealed class EfWorkOrderRepository(WorkManagementDbContext db) : IWorkOrderRepository
{
    public async Task<WorkOrder?> GetAsync(
        WorkOrderId id, CancellationToken cancellationToken = default) =>
        // Labour is an owned collection, so EF includes it automatically -- an owned type has no
        // existence apart from its owner and is never lazily absent. That is the aggregate
        // boundary showing up as a loading guarantee: you cannot accidentally get half an order.
        await db.WorkOrders.FirstOrDefaultAsync(order => order.Id == id, cancellationToken);

    public async Task AddAsync(WorkOrder workOrder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workOrder);
        await db.WorkOrders.AddAsync(workOrder, cancellationToken);
    }

    /// <summary>
    /// Open orders past their due date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two stages on purpose. The database narrows to rows that <em>could</em> have breached --
    /// a due date in the past and a status that is not terminal -- and then
    /// <see cref="WorkOrder.HasBreachedSla"/> makes the actual decision in the domain.
    /// </para>
    /// <para>
    /// The alternative is translating that rule into a LINQ predicate EF can push into SQL, which
    /// means the definition of "breached" would exist in two places and drift. The filter here is
    /// deliberately looser than the rule: it is allowed to return rows the domain then rejects,
    /// and it must never exclude a row the domain would have accepted.
    /// </para>
    /// <para>
    /// This stays correct as the backlog grows because the index behind it is filtered on exactly
    /// these statuses, so the scan is proportional to the open work rather than to the table.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<WorkOrder>> GetBreachingSlaAsync(
        DateTimeOffset asAt, CancellationToken cancellationToken = default)
    {
        var candidates = await db.WorkOrders
            .Where(order => order.DueBy != null
                            && order.DueBy < asAt
                            && order.Status != WorkOrderStatus.Completed
                            && order.Status != WorkOrderStatus.Cancelled)
            .ToListAsync(cancellationToken);

        return candidates.Where(order => order.HasBreachedSla(asAt)).ToArray();
    }

    /// <remarks>
    /// The predicate is pushed into SQL rather than filtered in memory like
    /// <see cref="GetBreachingSlaAsync"/> above, because every clause here maps to a column.
    /// <c>HasBreachedSla</c> cannot be translated -- it compares against a computed value -- so
    /// that one loads candidates and filters locally. This one has no such excuse, and a sweeper
    /// that pulls every scheduled order into memory every minute is a sweeper that gets slower
    /// as the business succeeds.
    /// </remarks>
    public async Task<IReadOnlyList<WorkOrder>> GetAwaitingReservationAsync(
        DateTimeOffset asOf, CancellationToken cancellationToken = default) =>
        await db.WorkOrders
            .Where(order => order.Status == WorkOrderStatus.Scheduled
                            && order.ReservationConfirmedAt == null
                            && order.ReservationDeadline != null
                            && order.ReservationDeadline < asOf)
            .ToListAsync(cancellationToken);
}

/// <summary>
/// The unit of work, which is now the DbContext's change tracker.
/// </summary>
/// <remarks>
/// <para>
/// <c>InMemoryUnitOfWork.SaveChangesAsync</c> returned 0 and did nothing, because a dictionary
/// write is already durable the instant it happens. Every call site was written to save anyway,
/// so none of them changed today -- the port existed precisely so this substitution would be
/// invisible.
/// </para>
/// <para>
/// It also means the aggregate mutations a use case performs now commit together. EF wraps a
/// single <c>SaveChangesAsync</c> in one transaction, so a work order and its new labour entry
/// are written atomically rather than one dictionary key at a time.
/// </para>
/// </remarks>
public sealed class EfUnitOfWork(WorkManagementDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
