namespace QuotesApi.Messaging;

/// <summary>
/// What the API publishes when something happens to a quote.
/// </summary>
/// <remarks>
/// <para>
/// One contract shared by the publisher and every subscriber. Subscribers must tolerate
/// fields they do not recognise — a topic with two subscriptions is two independently
/// deployed readers, and they will not upgrade at the same moment as the publisher.
/// </para>
/// <para>
/// <see cref="EventId"/> is the idempotency key. It is stamped as the Service Bus
/// <c>MessageId</c> at publish time and is what every consumer dedupes on. It belongs to the
/// event, not to the transport: a redelivery, a retry after a lock expiry, and a publisher
/// retrying a send that actually succeeded must all carry the same value, or dedupe achieves
/// nothing.
/// </para>
/// </remarks>
public sealed record QuoteEvent
{
    /// <summary>Stable identity of this event. Becomes the Service Bus MessageId.</summary>
    public required string EventId { get; init; }

    /// <summary>What happened. Subscribers filter and switch on this.</summary>
    public required string EventType { get; init; }

    public required int QuoteId { get; init; }
    public required string Author { get; init; }
    public required string Text { get; init; }

    /// <summary>When the event happened — not when it was delivered.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Set only by the test/demo endpoint. A message carrying this is designed to fail every
    /// time, so the dead-letter path can be demonstrated on demand rather than waited for.
    /// </summary>
    public bool Poison { get; init; }

    /// <summary>
    /// Set only by the test/demo endpoint. Makes the payload unreadable to the consumer, to
    /// exercise the *other* dead-letter route: rejected immediately rather than retried.
    /// </summary>
    public bool Malformed { get; init; }
}

public static class QuoteEventTypes
{
    public const string Created = "quote.created";
    public const string Deleted = "quote.deleted";
}
