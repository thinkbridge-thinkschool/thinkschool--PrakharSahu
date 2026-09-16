using Dispatch.Scheduling.Application.Reservations;
using Dispatch.Scheduling.Domain.Reservations;
using Dispatch.Scheduling.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Scheduling.Concurrency.Tests;

/// <summary>
/// Proves the double-booking race is closed, against a real database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this cannot be a unit test.</b> The race lives in the gap between a check and a write,
/// and that gap is a property of the <em>database's</em> view of the world. A fake closes it with
/// a <c>lock</c>, which proves the fake is good and nothing else. Day 29 learned this the
/// expensive way: a missing <c>SaveChanges</c> was invisible to every unit test because every
/// unit test used a dictionary, where mutating the object in the store <em>was</em> the save.
/// </para>
/// <para>
/// <b>Skipped rather than failed when no database is configured.</b> A test that cannot run is
/// not a test that passed, so it reports as skipped with the reason attached. Failing would make
/// a laptop with no Azure access look like a broken build; passing silently would be worse than
/// either.
/// </para>
/// </remarks>
public class ReservationConcurrencyTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Dispatch");

    private static bool Configured => !string.IsNullOrWhiteSpace(ConnectionString);

    private const string SkipReason =
        "Set ConnectionStrings__Dispatch to run the concurrency tests against a real SQL Server.";

    private static SchedulingDbContext NewContext() =>
        new(new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseSqlServer(ConnectionString, sql =>
            {
                sql.EnableRetryOnFailure(6, TimeSpan.FromSeconds(20), null);
                sql.MigrationsHistoryTable("__EFMigrationsHistory", SchedulingDbContext.Schema);
            })
            .Options);

    /// <summary>
    /// A technician nobody else is using, so concurrent test runs cannot collide with each other.
    /// </summary>
    private static TechnicianId FreshTechnician() => new(Guid.NewGuid());

    [SkippableFact]
    public async Task Two_concurrent_bookings_for_the_same_window_produce_exactly_one_reservation()
    {
        Skip.IfNot(Configured, SkipReason);

        var technician = FreshTechnician();
        var start = DateTimeOffset.UtcNow.AddDays(30);
        var end = start.AddHours(2);

        // Each task gets its own DbContext, because a DbContext is not thread-safe -- and because
        // that is what actually happens in production: one scope per request.
        var attempts = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = NewContext();
            var repository = new EfReservationRepository(db);

            var reservation = Reservation.Hold(Guid.NewGuid(), technician, start, end);
            return await repository.TryHoldAsync(reservation.Value);
        });

        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(1, outcomes.Count(o => o == ReservationOutcome.Reserved));
        Assert.Equal(7, outcomes.Count(o => o == ReservationOutcome.Conflict));

        await using var check = NewContext();
        var live = await check.Reservations
            .CountAsync(r => r.TechnicianId == technician && !r.IsReleased);

        Assert.Equal(1, live);
    }

    [SkippableFact]
    public async Task Concurrent_bookings_for_PARTIALLY_overlapping_windows_also_produce_one()
    {
        Skip.IfNot(Configured, SkipReason);

        // The case the unique index cannot catch. 09:00-11:00 and 10:00-12:00 are different rows
        // by every column, so only the serializable transaction's range lock stops the second.
        // If somebody ever "optimises" the isolation level away, this is the test that fails.
        var technician = FreshTechnician();
        var baseline = DateTimeOffset.UtcNow.AddDays(31);

        var attempts = Enumerable.Range(0, 6).Select(async offset =>
        {
            await using var db = NewContext();
            var repository = new EfReservationRepository(db);

            // Staggered by 20 minutes each, so every window overlaps its neighbours.
            var start = baseline.AddMinutes(20 * offset);
            var reservation = Reservation.Hold(Guid.NewGuid(), technician, start, start.AddHours(2));
            return await repository.TryHoldAsync(reservation.Value);
        });

        var outcomes = await Task.WhenAll(attempts);

        // Windows 0..5 all overlap the first two hours, so at most a couple can survive depending
        // on which commits first. The invariant is the one that matters: no two live reservations
        // for this technician overlap.
        Assert.Contains(ReservationOutcome.Reserved, outcomes);

        await using var check = NewContext();
        var live = await check.Reservations
            .Where(r => r.TechnicianId == technician && !r.IsReleased)
            .ToListAsync();

        foreach (var a in live)
        {
            foreach (var b in live.Where(other => other.Id != a.Id))
            {
                Assert.False(
                    a.Start < b.End && b.Start < a.End,
                    $"Two live reservations overlap: {a.Start:t}-{a.End:t} and {b.Start:t}-{b.End:t}");
            }
        }
    }

    [SkippableFact]
    public async Task A_released_slot_can_be_booked_again_with_the_identical_window()
    {
        Skip.IfNot(Configured, SkipReason);

        // The filtered unique index exists so that cancelling does not permanently poison a slot.
        // Without `[IsReleased] = 0` on the filter, this rebooking would collide with the dead row.
        var technician = FreshTechnician();
        var start = DateTimeOffset.UtcNow.AddDays(32);
        var end = start.AddHours(2);

        await using (var db = NewContext())
        {
            var repository = new EfReservationRepository(db);
            var first = Reservation.Hold(Guid.NewGuid(), technician, start, end).Value;
            Assert.Equal(ReservationOutcome.Reserved, await repository.TryHoldAsync(first));

            first.Release();
            await repository.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var repository = new EfReservationRepository(db);
            var second = Reservation.Hold(Guid.NewGuid(), technician, start, end).Value;

            Assert.Equal(ReservationOutcome.Reserved, await repository.TryHoldAsync(second));
        }
    }
}
