namespace QuotesApi.Messaging.Contracts;

/// <summary>
/// Stages an integration event for reliable publication.
/// </summary>
/// <remarks>
/// <para>
/// This interface lives in <b>Contracts</b> rather than in the Messaging module's Application
/// layer, and the reason is the one rule this solution is built around: a module may reference
/// another module's <c>Contracts</c> and nothing else.
/// </para>
/// <para>
/// The Quotes module has to stage an event in the same transaction as the quote it describes —
/// that is the whole point of the outbox. Putting the port here is what lets
/// <c>QuotesApi.Quotes.Infrastructure</c> depend on it without gaining any visibility of how
/// messaging works, which broker it talks to, or that an <c>OutboxMessage</c> entity exists.
/// </para>
/// </remarks>
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
