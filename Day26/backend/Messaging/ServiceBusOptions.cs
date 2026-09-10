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

    /// <summary>
    /// Namespace hostname, e.g. <c>sb-quotes-dev-abc123.servicebus.windows.net</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Day 26 addition, and the preferred path. When this is set the client authenticates with
    /// <c>DefaultAzureCredential</c> — locally that resolves to the signed-in Azure CLI user, and
    /// in Azure to the workload's managed identity. The namespace this connects to has
    /// <c>disableLocalAuth: true</c>, so a SAS connection string would be rejected even if one
    /// were configured.
    /// </para>
    /// <para>
    /// <see cref="ConnectionString"/> is kept rather than deleted so the Day 19-22 test suites,
    /// which construct options directly, keep compiling and running unchanged. It takes second
    /// place: if both are set, the namespace wins, because the token path is the one that
    /// survives a leaked key.
    /// </para>
    /// </remarks>
    public string FullyQualifiedNamespace { get; set; } = string.Empty;

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

    /// <summary>Messaging is on when either credential path is configured.</summary>
    public bool Enabled =>
        !string.IsNullOrWhiteSpace(FullyQualifiedNamespace)
        || !string.IsNullOrWhiteSpace(ConnectionString);

    /// <summary>True when the token path is in use rather than a SAS connection string.</summary>
    public bool UsesManagedIdentity => !string.IsNullOrWhiteSpace(FullyQualifiedNamespace);

    /// <summary>
    /// Authenticate with the signed-in Azure CLI user instead of the full credential chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set to <c>true</c> when running on a developer machine. It exists because
    /// <c>DefaultAzureCredential</c> does not degrade gracefully off Azure, and the way it fails
    /// is worth knowing.
    /// </para>
    /// <para>
    /// The chain tries <c>ManagedIdentityCredential</c> BEFORE <c>AzureCliCredential</c>. On a
    /// laptop that credential probes the Instance Metadata Service at the link-local address
    /// 169.254.169.254, which nothing answers, and after five retries it throws
    /// <c>AuthenticationFailedException</c>. A chained credential only moves to its next link on
    /// <c>CredentialUnavailableException</c> — a *failed* authentication is treated as fatal — so
    /// the chain aborts and never reaches the CLI credential that would have worked.
    /// </para>
    /// <para>
    /// The observed symptom was not an auth error. It was an outbox that claimed rows and never
    /// published them, retrying forever, with the real cause buried under a thousand lines of
    /// MSAL cache logging:
    /// </para>
    /// <code>
    /// ManagedIdentityCredential authentication failed: All Managed Identity sources are
    /// unavailable. The Azure Instance Metadata Service (IMDS) that runs on VMs was not detected
    /// </code>
    /// <para>
    /// In Azure this stays <c>false</c> and the workload's managed identity is used, which is the
    /// point of Day 25. This is a switch for where the code runs, not a change to how it
    /// authenticates in production.
    /// </para>
    /// </remarks>
    public bool UseAzureCliCredential { get; set; }
}
