using System.Diagnostics;
using System.Text.Json;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Outbox;

public interface IOutboxWriter
{
    /// <summary>
    /// Stages an event for publication. <b>Does not save.</b>
    /// </summary>
    /// <returns>
    /// The id the event will be published under — the consumer's idempotency key.
    /// </returns>
    Guid Enqueue(
        string type,
        string aggregateType,
        string aggregateId,
        object payload,
        DateTimeOffset occurredAt);
}

/// <summary>
/// Adds outbox rows to the caller's <see cref="AppDbContext"/>, and deliberately leaves the
/// saving to the caller.
/// </summary>
/// <remarks>
/// <para><b>Why this does not call SaveChangesAsync.</b> That omission is the entire pattern.
/// The atomicity comes from the domain change and the outbox row being tracked by the same
/// DbContext and flushed by the same <c>SaveChangesAsync</c> — EF wraps a single SaveChanges
/// in one transaction, so both rows commit together or neither does.</para>
///
/// <para>Saving here would break it in a way that still looks correct in every test that does
/// not kill the process: the outbox row would commit in its own transaction, and a failure in
/// the caller's later save would leave an event describing a quote that was never created.
/// That is the dual write again, merely reversed.</para>
///
/// <para>Scoped, because <see cref="AppDbContext"/> is. It has to be the <em>same</em> context
/// instance the endpoint is using, or the two writes land in different transactions and the
/// guarantee evaporates silently.</para>
/// </remarks>
public sealed class OutboxWriter : IOutboxWriter
{
    private readonly AppDbContext _db;
    private readonly ILogger<OutboxWriter> _logger;

    public OutboxWriter(AppDbContext db, ILogger<OutboxWriter> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Guid Enqueue(
        string type,
        string aggregateType,
        string aggregateId,
        object payload,
        DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(payload);

        var message = new OutboxMessage
        {
            // Generated once, here, and never again. Every republish after a crash carries
            // this same value, which is what lets the consumer recognise a duplicate instead
            // of doing the work twice.
            Id = Guid.NewGuid(),
            Type = type,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            Payload = JsonSerializer.Serialize(payload),
            // Normalised to a UTC instant; see OutboxMessage.OccurredAt for why the column
            // is a DateTime rather than a DateTimeOffset.
            OccurredAt = occurredAt.UtcDateTime,
            ProcessedAt = null,
            Attempts = 0,

            // -------------------------------------------------------------------------------
            // Day 26: capture the trace context, here, while it still exists.
            //
            // This is the only moment it is available. Right now we are inside the API request,
            // so Activity.Current is the request span and Id is its W3C traceparent. By the time
            // the relay reads this row — different process, different thread, minutes later —
            // Activity.Current is null or belongs to the relay's own sweep, and the connection
            // to the originating request is unrecoverable.
            //
            // `?.Id` and not `Activity.Current!.Id`: a message enqueued by a background process
            // or a test has no ambient activity, and null is the correct answer there rather
            // than a NullReferenceException on a business write. Telemetry must never be the
            // reason a domain operation fails.
            // -------------------------------------------------------------------------------
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString
        };

        // Add, not AddAsync + Save. The row joins whatever transaction the caller's
        // SaveChangesAsync opens.
        _db.OutboxMessages.Add(message);

        _logger.LogDebug(
            "Staged outbox message {MessageId} ({Type}) for {AggregateType} {AggregateId}.",
            message.Id, type, aggregateType, aggregateId);

        return message.Id;
    }
}
