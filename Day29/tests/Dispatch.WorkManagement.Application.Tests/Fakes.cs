using System.Collections.Concurrent;
using Dispatch.Billing.Application.Invoices;
using Dispatch.Billing.Domain.Invoices;
using Dispatch.Scheduling.Application.Reservations;
using Dispatch.Scheduling.Domain.Reservations;
// `TechnicianId` exists in BOTH Scheduling.Domain and WorkManagement.Domain, and that is the
// design working rather than a naming slip: in Scheduling a technician is the entity the module
// is organised around, in WorkManagement it is an id and nothing more. This file touches both
// contexts, so it has to say which one it means -- the compiler will not guess.
using SchedulingTechnicianId = Dispatch.Scheduling.Domain.Reservations.TechnicianId;
using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Application.Abstractions;
using Dispatch.WorkManagement.Domain.WorkOrders;

namespace Dispatch.WorkManagement.Application.Tests;

// =================================================================================================
// In-memory implementations of the three repository ports, living in the TEST project.
//
// They used to live in the production Infrastructure projects, and these tests referenced those
// projects to borrow them. That was wrong in a way that only became visible today: replacing the
// dictionaries with EF Core deleted the classes the tests depended on, and the build broke in the
// test project rather than anywhere real.
//
// A test double is part of the test, not part of the system. Moving them here means:
//
//   * shipping infrastructure no longer contains a fake store that production never uses
//   * these tests exercise the APPLICATION layer against its ports, which is what they are for
//   * swapping the real implementation cannot break them again, because they do not know it exists
//
// The project references to the three Infrastructure projects are gone with them.
// =================================================================================================

internal sealed class FakeWorkOrderRepository : IWorkOrderRepository
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

    public Task<IReadOnlyList<WorkOrder>> GetBreachingSlaAsync(
        DateTimeOffset asAt, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkOrder>>(
            _orders.Values.Where(order => order.HasBreachedSla(asAt)).ToArray());
}

/// <remarks>
/// Returns 0 because there is nothing to flush: a dictionary write is durable the moment it
/// happens. The real one returns the number of rows EF wrote. No test asserts on the number,
/// which is correct -- a use case should not care how many rows its intent became.
/// </remarks>
internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}

internal sealed class FakeReservationRepository : IReservationRepository
{
    private readonly ConcurrentDictionary<ReservationId, Reservation> _reservations = new();

    public Task<bool> HasOverlapAsync(
        SchedulingTechnicianId technicianId, DateTimeOffset start, DateTimeOffset end,
        CancellationToken ct = default) =>
        Task.FromResult(_reservations.Values.Any(r =>
            r.TechnicianId == technicianId && r.Overlaps(start, end)));

    public Task<Reservation?> GetByWorkOrderAsync(Guid workOrderId, CancellationToken ct = default) =>
        Task.FromResult(_reservations.Values.FirstOrDefault(r => r.WorkOrderId == workOrderId));

    public Task AddAsync(Reservation reservation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        _reservations[reservation.Id] = reservation;
        return Task.CompletedTask;
    }

    // Nothing to flush: the dictionary holds the entity itself, so a mutation is already visible.
    // That is exactly why the missing save in WorkOrderReleasedHandler was invisible to these
    // tests -- a fake that cannot reproduce a failure mode cannot warn you about it.
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeInvoiceRepository : IInvoiceRepository
{
    private readonly ConcurrentDictionary<InvoiceId, Invoice> _invoices = new();

    public Task<Invoice?> GetByWorkOrderAsync(Guid workOrderId, CancellationToken ct = default) =>
        Task.FromResult(_invoices.Values.FirstOrDefault(i => i.WorkOrderId == workOrderId));

    public Task AddAsync(Invoice invoice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        _invoices[invoice.Id] = invoice;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Invoice>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Invoice>>(_invoices.Values.ToArray());
}
