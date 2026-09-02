using System.Collections.Concurrent;

namespace QuotesApi.Messaging.Handlers;

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

public sealed class InMemoryProjectionStore : IProjectionStore
{
    private readonly ConcurrentQueue<string> _audit = new();
    private readonly ConcurrentDictionary<int, string> _index = new();

    public void RecordAudit(string line)
    {
        _audit.Enqueue(line);
        while (_audit.Count > 500 && _audit.TryDequeue(out _)) { }
    }

    // Indexing the same quote twice overwrites rather than appends. Naturally idempotent —
    // the shape of sink you want when redelivery is a normal event rather than an error.
    public void RecordIndexed(int quoteId, string author, string text) =>
        _index[quoteId] = $"{author}: {text}";

    public IReadOnlyList<string> AuditLog => _audit.ToArray();
    public IReadOnlyDictionary<int, string> SearchIndex => _index;
}

/// <summary>Subscription 1 — appends every event to an audit trail.</summary>
public sealed class AuditProjectionHandler : ISubscriptionHandler
{
    private readonly IProjectionStore _store;
    private readonly ServiceBusOptions _options;
    private readonly ILogger<AuditProjectionHandler> _logger;

    public AuditProjectionHandler(
        IProjectionStore store,
        Microsoft.Extensions.Options.IOptions<ServiceBusOptions> options,
        ILogger<AuditProjectionHandler> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public string SubscriptionName => _options.AuditSubscription;

    public async Task HandleAsync(QuoteEvent @event, CancellationToken cancellationToken)
    {
        // A poison event fails here every time, on purpose. Retrying is the correct response
        // to a transient fault, and the whole point of the demonstration is that the broker
        // eventually gives up and dead-letters rather than retrying forever.
        if (@event.Poison)
        {
            throw new InvalidOperationException(
                $"Poison event {@event.EventId}: this handler fails on every delivery, by design.");
        }

        await Task.Delay(50, cancellationToken);   // stands in for a write

        _store.RecordAudit(
            $"{@event.OccurredAt:O} {@event.EventType} quote={@event.QuoteId} author={@event.Author}");

        _logger.LogInformation(
            "[audit] recorded {EventType} for quote {QuoteId}.", @event.EventType, @event.QuoteId);
    }
}

/// <summary>
/// Subscription 2 — maintains a search index over the same events.
/// </summary>
/// <remarks>
/// Receives every message the audit handler does, from its own copy on its own subscription.
/// It is the reason dedupe is keyed by subscription: share the key and this handler skips
/// every message audit reached first, leaving the index permanently and silently incomplete.
/// </remarks>
public sealed class SearchIndexHandler : ISubscriptionHandler
{
    private readonly IProjectionStore _store;
    private readonly ServiceBusOptions _options;
    private readonly ILogger<SearchIndexHandler> _logger;

    public SearchIndexHandler(
        IProjectionStore store,
        Microsoft.Extensions.Options.IOptions<ServiceBusOptions> options,
        ILogger<SearchIndexHandler> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public string SubscriptionName => _options.SearchIndexSubscription;

    public async Task HandleAsync(QuoteEvent @event, CancellationToken cancellationToken)
    {
        if (@event.Poison)
        {
            throw new InvalidOperationException(
                $"Poison event {@event.EventId}: this handler fails on every delivery, by design.");
        }

        await Task.Delay(50, cancellationToken);
        _store.RecordIndexed(@event.QuoteId, @event.Author, @event.Text);

        _logger.LogInformation("[search-index] indexed quote {QuoteId}.", @event.QuoteId);
    }
}
