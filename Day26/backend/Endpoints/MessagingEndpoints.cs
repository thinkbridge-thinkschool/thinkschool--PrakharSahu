using Microsoft.Extensions.Options;
using QuotesApi.Messaging;
using QuotesApi.Messaging.Handlers;
using QuotesApi.Models;
using QuotesApi.Services;

namespace QuotesApi.Endpoints;

/// <summary>Publishes a test event. The demo/verification entry point.</summary>
/// <param name="Count">How many events to publish, for the competing-consumer demonstration.</param>
/// <param name="Poison">Make the handlers throw on every delivery, to force the DLQ path.</param>
/// <param name="Malformed">Send a body no consumer can parse, to force immediate dead-lettering.</param>
/// <param name="EventId">Reuse an id to demonstrate duplicate suppression.</param>
public sealed record PublishTestEventRequest(
    int? Count, bool? Poison, bool? Malformed, string? EventId, string? Author, string? Text);

public static class MessagingEndpoints
{
    public static void MapMessagingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/messaging");

        // -----------------------------------------------------------------------------
        // POST /api/messaging/publish
        //
        // Publishing an event with an explicit EventId twice is how duplicate suppression is
        // demonstrated: the broker delivers both, and each subscription runs the handler once.
        // -----------------------------------------------------------------------------
        group.MapPost("/publish", async (
            PublishTestEventRequest? request,
            IEventPublisher publisher,
            IOptions<ServiceBusOptions> options,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            if (!options.Value.Enabled)
            {
                return Results.Json(
                    new DomainError("Messaging is disabled: no ServiceBus:ConnectionString is configured."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var count = Math.Clamp(request?.Count ?? 1, 1, 50);
            var published = new List<string>(count);

            for (var i = 0; i < count; i++)
            {
                var @event = new QuoteEvent
                {
                    // A caller-supplied id is reused verbatim across every message in the
                    // batch — which is exactly how you ask for duplicates on purpose.
                    EventId = request?.EventId ?? Guid.NewGuid().ToString(),
                    EventType = QuoteEventTypes.Created,
                    QuoteId = 9000 + i,
                    Author = request?.Author ?? "Test Author",
                    Text = request?.Text ?? $"Test event {i + 1} of {count}.",
                    OccurredAt = clock.UtcNow,
                    Poison = request?.Poison ?? false,
                    Malformed = request?.Malformed ?? false
                };

                published.Add(await publisher.PublishAsync(@event, cancellationToken));
            }

            return Results.Accepted("/api/messaging/projections", new
            {
                published = published.Count,
                messageIds = published,
                note = "Both subscriptions receive every message. Poll /api/messaging/projections."
            });
        }).RequireAuthorization();

        // GET /api/messaging/projections — what each subscription actually did.
        //
        // The proof of fan-out: one publish, two independent readers, both with output.
        group.MapGet("/projections", (
            IProjectionStore store,
            IProcessedMessageTracker tracker,
            IOptions<ServiceBusOptions> options) =>
        {
            var config = options.Value;
            return Results.Ok(new
            {
                enabled = config.Enabled,
                topic = config.TopicName,
                subscriptions = new object[]
                {
                    new
                    {
                        name = config.AuditSubscription,
                        processed = tracker.ProcessedCount(config.AuditSubscription),
                        duplicatesSuppressed = tracker.DuplicatesSuppressed(config.AuditSubscription),
                        auditLog = store.AuditLog.TakeLast(20)
                    },
                    new
                    {
                        name = config.SearchIndexSubscription,
                        processed = tracker.ProcessedCount(config.SearchIndexSubscription),
                        duplicatesSuppressed = tracker.DuplicatesSuppressed(config.SearchIndexSubscription),
                        indexedQuotes = store.SearchIndex.Count,
                        sample = store.SearchIndex.Take(20).Select(kv => $"{kv.Key}: {kv.Value}")
                    }
                }
            });
        });

        // -----------------------------------------------------------------------------
        // GET /api/messaging/dlq/{subscription} — the dead-letter proof.
        //
        // DeadLetterReason distinguishes the two routes into this queue:
        //   MaxDeliveryCountExceeded — retried to the limit and given up on by the broker
        //   MalformedPayload         — rejected outright by the consumer, never retried
        // -----------------------------------------------------------------------------
        group.MapGet("/dlq/{subscription}", async (
            string subscription,
            IDeadLetterReader reader,
            IOptions<ServiceBusOptions> options,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            if (!options.Value.Enabled)
            {
                return Results.Json(
                    new DomainError("Messaging is disabled."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var known = new[] { options.Value.AuditSubscription, options.Value.SearchIndexSubscription };
            if (!known.Contains(subscription, StringComparer.OrdinalIgnoreCase))
            {
                return Results.NotFound(new DomainError(
                    $"Unknown subscription '{subscription}'. Known: {string.Join(", ", known)}."));
            }

            var entries = await reader.PeekAsync(
                subscription, Math.Clamp(limit ?? 20, 1, 100), cancellationToken);

            return Results.Ok(new { subscription, deadLetterCount = entries.Count, messages = entries });
        });

        // DELETE /api/messaging/dlq/{subscription} — drain the DLQ so a verification run
        // starts clean. Deliberate and authorized; dead letters are evidence, not litter.
        group.MapDelete("/dlq/{subscription}", async (
            string subscription,
            IDeadLetterReader reader,
            IOptions<ServiceBusOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!options.Value.Enabled)
            {
                return Results.Json(
                    new DomainError("Messaging is disabled."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var purged = await reader.PurgeAsync(subscription, cancellationToken);
            return Results.Ok(new { subscription, purged });
        }).RequireAuthorization();
    }
}
