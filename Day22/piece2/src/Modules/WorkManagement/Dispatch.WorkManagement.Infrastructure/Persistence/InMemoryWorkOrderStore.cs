using System.Collections.Concurrent;
using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Application.Abstractions;
using Dispatch.WorkManagement.Domain.WorkOrders;

namespace Dispatch.WorkManagement.Infrastructure.Persistence;

/// <summary>
/// The adapter behind <see cref="IWorkOrderRepository"/>, backed by a dictionary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Persistence is deliberately not chosen yet.</b> This is a kickoff scaffold, and picking a
/// database on day one means picking it before the aggregate boundaries have been tested against
/// a single real requirement. Every schema decision made now is a migration to undo later.
/// </para>
/// <para>
/// What matters is that the decision is <em>deferrable</em>, and the structure is what makes it
/// so. Nothing above this layer knows a dictionary is here: Application depends on the port,
/// Domain depends on neither. Swapping in EF Core, Dapper or Marten is a change to this file and
/// <see cref="InMemoryUnitOfWork"/>, and to nothing else — which is the claim clean architecture
/// makes and rarely gets asked to demonstrate.
/// </para>
/// <para>
/// The honest limitations, so nobody mistakes this for finished work: no transactions, no
/// concurrency control, and everything is lost on restart. Day 23 replaces it.
/// </para>
/// </remarks>
public sealed class InMemoryWorkOrderStore : IWorkOrderRepository
{
    private readonly ConcurrentDictionary<WorkOrderId, WorkOrder> _orders = new();

    public Task<WorkOrder?> GetAsync(WorkOrderId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_orders.GetValueOrDefault(id));

    public Task AddAsync(WorkOrder workOrder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workOrder);
        _orders[workOrder.Id] = workOrder;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Open orders whose SLA deadline has passed.
    /// </summary>
    /// <remarks>
    /// The filter is <see cref="WorkOrder.HasBreachedSla"/> — the aggregate's own rule, called
    /// here rather than reimplemented as a predicate. A repository that rewrites domain logic in
    /// query form is how two definitions of "breached" come to exist and quietly disagree.
    ///
    /// Against a real database this becomes a translated query, and keeping the rule callable in
    /// both shapes is a genuine tension. The resolution is a specification the domain owns, not
    /// a second copy of the rule in a WHERE clause.
    /// </remarks>
    public Task<IReadOnlyList<WorkOrder>> GetBreachingSlaAsync(
        DateTimeOffset asAt, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkOrder>>(
            _orders.Values.Where(order => order.HasBreachedSla(asAt)).ToArray());
}

/// <summary>
/// Stands in for a transaction until there is a database to have one in.
/// </summary>
/// <remarks>
/// It does nothing, and it is here anyway. The interface is what the use cases are written
/// against, so introducing a real transaction later changes this class and no caller — whereas
/// adding the concept afterwards would mean revisiting every use case to find where a commit
/// should have been.
/// </remarks>
public sealed class InMemoryUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
}
