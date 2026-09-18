using QuotesApi.Quotes.Contracts;

namespace QuotesApi.Messaging.Application;

/// <summary>Does the work for one subscription.</summary>
/// <remarks>
/// Handlers are resolved from a scope per message, exactly as Day 18's job handlers are, so
/// they may depend on scoped services. A handler must be safe to run twice — dedupe protects
/// it inside one replica, and nothing protects it across replicas.
/// </remarks>
public interface ISubscriptionHandler
{
    /// <summary>Which subscription this handler drains. Matched against configuration.</summary>
    string SubscriptionName { get; }

    Task HandleAsync(QuoteEvent @event, CancellationToken cancellationToken);
}

/// <summary>
/// What the two subscriptions produced, so the fan-out can be observed from outside.
/// </summary>
/// <remarks>
/// Stands in for the real sinks — an audit table and a search index. Kept in memory because
/// the point being demonstrated is the messaging topology, and a second database would add
/// nothing to it but setup.
/// </remarks>
public interface IProjectionStore
{
    void RecordAudit(string line);
    void RecordIndexed(int quoteId, string author, string text);

    IReadOnlyList<string> AuditLog { get; }
    IReadOnlyDictionary<int, string> SearchIndex { get; }
}
