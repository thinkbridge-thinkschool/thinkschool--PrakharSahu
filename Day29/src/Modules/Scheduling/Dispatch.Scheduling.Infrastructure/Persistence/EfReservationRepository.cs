using Dispatch.Scheduling.Application.Reservations;
using Dispatch.Scheduling.Domain.Reservations;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Scheduling.Infrastructure.Persistence;

/// <summary>
/// The reservation repository, backed by EF Core.
/// </summary>
public sealed class EfReservationRepository(SchedulingDbContext db) : IReservationRepository
{
    /// <summary>
    /// Whether the technician already has a live reservation touching this window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The overlap test is <c>start &lt; existing.End &amp;&amp; existing.Start &lt; end</c> —
    /// half-open intervals, so a booking ending at 10:00 and one starting at 10:00 do not
    /// conflict. That matches <c>Reservation.Overlaps</c>, and it has to: two different answers
    /// to "do these overlap" is a bug that only shows up as a double booking.
    /// </para>
    /// <para>
    /// <b>This check still races, and that is a known gap rather than an oversight.</b> Two
    /// concurrent requests can both find no overlap and both insert. The fix is a database
    /// constraint, not more C# — a unique or exclusion constraint that makes the second insert
    /// fail so the existing reservation-failed path handles it. That is build-plan day 2.
    /// </para>
    /// </remarks>
    public async Task<bool> HasOverlapAsync(
        TechnicianId technicianId, DateTimeOffset start, DateTimeOffset end,
        CancellationToken ct = default) =>
        await db.Reservations.AnyAsync(
            reservation => !reservation.IsReleased
                           && reservation.TechnicianId == technicianId
                           && start < reservation.End
                           && reservation.Start < end,
            ct);

    public async Task<Reservation?> GetByWorkOrderAsync(
        Guid workOrderId, CancellationToken ct = default) =>
        await db.Reservations.FirstOrDefaultAsync(
            reservation => reservation.WorkOrderId == workOrderId, ct);

    /// <remarks>
    /// Saves immediately, because this module has no unit-of-work port. The dictionary version
    /// did too — a write was durable the moment it happened — so no call site changes. When the
    /// outbox arrives on day 3 this becomes a staged write inside the caller's transaction, and
    /// that will be a deliberate change rather than a silent one.
    /// </remarks>
    public async Task AddAsync(Reservation reservation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        await db.Reservations.AddAsync(reservation, ct);
        await db.SaveChangesAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
