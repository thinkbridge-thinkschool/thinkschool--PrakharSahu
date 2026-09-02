using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Outbox;

namespace QuotesApi.Endpoints;

public sealed record ArmFaultRequest(string Mode, int? Occurrences);

public static class OutboxEndpoints
{
    public static void MapOutboxEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/outbox");

        // GET /api/outbox — the state of the outbox, which is the whole proof surface.
        //
        // "pending" is the number that matters. In a healthy system it hovers near zero; a
        // rising value means the relay is down or the broker is refusing, and it is visible
        // here long before anyone notices a stale projection downstream.
        group.MapGet("/", async (AppDbContext db, int? limit, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 20, 1, 100);

            var pending = await db.OutboxMessages.CountAsync(m => m.ProcessedAt == null, ct);
            var processed = await db.OutboxMessages.CountAsync(m => m.ProcessedAt != null, ct);

            var recent = await db.OutboxMessages
                .OrderByDescending(m => m.OccurredAt)
                .Take(take)
                .Select(m => new
                {
                    m.Id,
                    m.Type,
                    m.AggregateType,
                    m.AggregateId,
                    status = m.ProcessedAt == null ? "Pending" : "Published",
                    m.OccurredAt,
                    m.ProcessedAt,
                    m.Attempts,
                    m.LastError,
                    m.LockedBy,
                    m.LockedUntil,
                    m.NextAttemptAt
                })
                .ToListAsync(ct);

            return Results.Ok(new { pending, processed, total = pending + processed, recent });
        });

        // GET /api/outbox/{id} — one row, for asserting on a specific message across a restart.
        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var message = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == id, ct);
            return message is null
                ? Results.NotFound(new DomainError("No such outbox message."))
                : Results.Ok(new
                {
                    message.Id,
                    message.Type,
                    message.AggregateType,
                    message.AggregateId,
                    status = message.ProcessedAt == null ? "Pending" : "Published",
                    message.OccurredAt,
                    message.ProcessedAt,
                    message.Attempts,
                    message.LastError,
                    message.Payload
                });
        });

        // -----------------------------------------------------------------------------
        // POST /api/outbox/faults — arm a crash in the relay.
        //
        // Development only. An endpoint that can make production stop delivering messages is
        // a liability no matter how it is named, and the environment check is the only thing
        // standing between "useful demo" and "outage on request".
        // -----------------------------------------------------------------------------
        group.MapPost("/faults", (
            ArmFaultRequest request,
            OutboxFaults faults,
            IWebHostEnvironment environment,
            ILogger<Program> logger) =>
        {
            if (!environment.IsDevelopment())
            {
                return Results.NotFound();
            }

            if (!Enum.TryParse<OutboxFaultMode>(request.Mode, ignoreCase: true, out var mode))
            {
                return Results.BadRequest(new DomainError(
                    $"Unknown fault mode '{request.Mode}'. Valid: {string.Join(", ", Enum.GetNames<OutboxFaultMode>())}."));
            }

            if (mode == OutboxFaultMode.None)
            {
                faults.Disarm();
                logger.LogWarning("Outbox fault injection disarmed.");
                return Results.Ok(new { mode = "None", armed = false });
            }

            var occurrences = Math.Clamp(request.Occurrences ?? 1, 1, 100);
            faults.Arm(mode, occurrences);

            logger.LogWarning(
                "Outbox fault ARMED: {Mode} for the next {Occurrences} message(s).", mode, occurrences);

            return Results.Ok(new { mode = mode.ToString(), occurrences, armed = true });
        }).RequireAuthorization();

        group.MapGet("/faults", (OutboxFaults faults) =>
            Results.Ok(new { mode = faults.Mode.ToString(), remaining = faults.Remaining }));
    }
}
