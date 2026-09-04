using System.Collections.Concurrent;
using Dispatch.Scheduling.Application.Reservations;
using Dispatch.Scheduling.Domain.Reservations;

namespace Dispatch.Scheduling.Infrastructure.Persistence;

/// <summary>
/// Reservations, in a dictionary. Same deliberate deferral as WorkManagement's store.
/// </summary>
/// <remarks>
/// The overlap check lives here rather than in the aggregate because it is a set-wide invariant:
/// no single reservation can see its neighbours. Against a real database this becomes an
/// exclusion constraint or a serializable read -- checking in application code alone would let
/// two concurrent bookings both pass the check and both insert.
///
/// Worth stating plainly: THIS IMPLEMENTATION HAS THAT RACE. It is correct for a single-threaded
/// scaffold and wrong under load, and the fix is a database constraint, not more C#.
/// </remarks>
public sealed class InMemoryReservationStore : IReservationRepository
{
    private readonly ConcurrentDictionary<ReservationId, Reservation> _reservations = new();

    public Task<bool> HasOverlapAsync(
        TechnicianId technicianId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
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
}
