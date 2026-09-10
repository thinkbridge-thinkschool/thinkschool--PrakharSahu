namespace QuotesApi.Caching;

/// <summary>
/// Cache configuration, held as a mutable singleton so the load test can flip it at runtime.
/// </summary>
/// <remarks>
/// Deliberately not <c>IOptions&lt;T&gt;</c>. The exercise needs a before/after comparison, and
/// the only way to make those two numbers comparable is to change exactly one thing — so the
/// "before" arm must be the same binary, the same endpoint and the same request, with caching
/// switched off. Restarting with different configuration would also change JIT warmth,
/// connection-pool state and OS page cache, and the difference would no longer be attributable
/// to the cache alone.
/// </remarks>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>Master switch. Off means every read goes to the database.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>L2 (Redis) lifetime — how long a value survives a process restart.</summary>
    public TimeSpan Expiration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// L1 (in-memory) lifetime. Shorter than <see cref="Expiration"/> on purpose.
    /// </summary>
    /// <remarks>
    /// L1 is per-process and cannot be invalidated from another instance, so this value is the
    /// worst-case staleness window across replicas. Long L1 is fast and stale; short L1 is fresh
    /// and costs more Redis round trips. It is the main dial worth understanding here.
    /// </remarks>
    public TimeSpan LocalExpiration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Artificial delay inside the factory, so a stampede is observable at all.
    /// </summary>
    /// <remarks>
    /// The demo database is a handful of rows in SQLite and a real query returns in
    /// microseconds — far too fast for concurrent callers to overlap, so an unprotected
    /// stampede would simply not show up. This makes the factory cost what a realistic
    /// expensive read costs.
    /// </remarks>
    public TimeSpan SimulatedQueryCost { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Set at startup from whether a Redis connection string was configured.</summary>
    public bool RedisConnected { get; set; }

    public string Layers => RedisConnected ? "L1 in-memory + L2 Redis" : "L1 in-memory only";
}
