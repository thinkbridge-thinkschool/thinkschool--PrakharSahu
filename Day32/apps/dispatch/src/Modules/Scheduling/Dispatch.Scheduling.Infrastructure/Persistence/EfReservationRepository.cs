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
    /// <b>This method is a read, and on its own it still races.</b> Day 29 recorded that as a
    /// known gap; it is no longer how a booking is made. <see cref="TryHoldAsync"/> is the
    /// write path and closes the race by making the check and the insert one transaction. This
    /// one remains for queries that only want to look — a calendar view, a diagnostic — where a
    /// momentarily stale answer costs nothing.
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

    /// <summary>
    /// The overlap check and the insert, as one indivisible operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two mechanisms, because one is not enough.</b>
    /// </para>
    /// <para>
    /// <b>1. A serializable transaction.</b> Under <c>Serializable</c>, SQL Server takes <em>range
    /// locks</em> over the rows the overlap query examined — including rows that do not exist yet.
    /// A second transaction trying to insert into that range blocks until the first commits, and
    /// then its own overlap query sees the new row. That is what removes the window between the
    /// check and the write; no amount of C# can do it, because the gap is in the database's view
    /// of the world rather than in the program's.
    /// </para>
    /// <para>
    /// <b>2. A unique index as a backstop.</b> Range locks depend on the query being able to take
    /// them, which depends on an index and on the isolation level actually being applied. If
    /// either assumption is ever wrong — a provider change, a migration that drops the filtered
    /// index — the unique constraint still refuses the duplicate. Defence in depth, because the
    /// failure mode here is silent and the cost of the second mechanism is one index.
    /// </para>
    /// <para>
    /// The unique index only catches an <em>exact</em> duplicate window, not a partial overlap.
    /// SQL Server has no exclusion constraints, so partial overlap is the serializable
    /// transaction's job alone. Stating that plainly rather than implying the index does more
    /// than it does.
    /// </para>
    /// <para>
    /// A deadlock or a serialization failure surfaces as <see cref="ReservationOutcome.Conflict"/>
    /// rather than an exception: from the caller's point of view "somebody else got this slot" and
    /// "we collided deciding who gets this slot" are the same answer, and both are handled by the
    /// reservation-failed path that already exists.
    /// </para>
    /// </remarks>
    public async Task<ReservationOutcome> TryHoldAsync(
        Reservation reservation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        // The execution strategy owns the retry loop (Azure SQL is retried on transient faults),
        // and an explicit transaction inside a retried operation must be created by that strategy
        // or EF throws. This is the documented shape, not defensive decoration.
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);

            try
            {
                var conflict = await db.Reservations.AnyAsync(
                    existing => !existing.IsReleased
                                && existing.TechnicianId == reservation.TechnicianId
                                && reservation.Start < existing.End
                                && existing.Start < reservation.End,
                    ct);

                if (conflict)
                {
                    await transaction.RollbackAsync(ct);
                    return ReservationOutcome.Conflict;
                }

                await db.Reservations.AddAsync(reservation, ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return ReservationOutcome.Reserved;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // The backstop fired: another transaction committed the identical window between
                // this one's check and its insert.
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return ReservationOutcome.Conflict;
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (IsDeadlockOrSerializationFailure(ex))
            {
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return ReservationOutcome.Conflict;
            }
        });
    }

    /// <summary>2627 is a unique constraint violation, 2601 a unique index violation.</summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is Microsoft.Data.SqlClient.SqlException sql
        && sql.Errors.Cast<Microsoft.Data.SqlClient.SqlError>()
              .Any(error => error.Number is 2601 or 2627);

    /// <summary>1205 is a deadlock victim; 3960 a snapshot update conflict.</summary>
    private static bool IsDeadlockOrSerializationFailure(Microsoft.Data.SqlClient.SqlException exception) =>
        exception.Errors.Cast<Microsoft.Data.SqlClient.SqlError>()
                 .Any(error => error.Number is 1205 or 3960);
}
