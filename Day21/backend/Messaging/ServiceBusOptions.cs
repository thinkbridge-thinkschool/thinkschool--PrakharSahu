namespace QuotesApi.Messaging;

/// <summary>
/// Everything the messaging layer needs. Bound from the <c>ServiceBus</c> configuration
/// section.
/// </summary>
/// <remarks>
/// The connection string is the one secret here and never appears in this repository. Locally
/// it is the emulator's fixed development string; in Azure it is a Container Apps secret. The
/// emulator only speaks connection strings, which is why this is not managed identity — see
/// EXERCISE.md.
/// </remarks>
public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    /// <summary>
    /// Leave empty to disable messaging entirely.
    /// </summary>
    /// <remarks>
    /// The same switch Day 17 used for caller-identity enforcement, for the same reason: the
    /// Week-1 API, its tests and a plain <c>dotnet run</c> must all still work on a machine
    /// with no broker anywhere near it. Absent configuration disables the feature rather than
    /// failing the boot.
    /// </remarks>
    public string ConnectionString { get; set; } = string.Empty;

    public string TopicName { get; set; } = "quote-events";

    /// <summary>
    /// The two subscriptions. Both receive every message — that is the point of a topic over
    /// a queue.
    /// </summary>
    public string AuditSubscription { get; set; } = "audit";
    public string SearchIndexSubscription { get; set; } = "search-index";

    /// <summary>
    /// How many messages one processor handles at once.
    /// </summary>
    /// <remarks>
    /// Concurrency <em>within</em> a consumer. Combined with <see cref="ConsumersPerSubscription"/>
    /// it is what makes this a competing-consumer setup: Service Bus hands each message to
    /// exactly one of them, so adding either number adds throughput without duplicating work.
    /// </remarks>
    public int MaxConcurrentCalls { get; set; } = 2;

    /// <summary>
    /// How many independent processors run against each subscription.
    /// </summary>
    /// <remarks>
    /// In production these would be separate replicas. Running more than one in a single
    /// process is what lets a laptop demonstrate that competing consumers do not double-process
    /// — each message is still handled exactly once per subscription.
    /// </remarks>
    public int ConsumersPerSubscription { get; set; } = 2;

    /// <summary>
    /// Mirrors the broker's own MaxDeliveryCount, for logging only.
    /// </summary>
    /// <remarks>
    /// The real limit lives on the subscription in Azure (or in the emulator's Config.json),
    /// not here — the broker counts deliveries and moves the message, and no client setting
    /// can override that. This exists so the logs can say "attempt 2 of 3" instead of
    /// "attempt 2 of ?". If the two drift apart the logs are wrong, not the behaviour.
    /// </remarks>
    public int MaxDeliveryCount { get; set; } = 3;

    public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);
}
