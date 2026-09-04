namespace QuotesApi.Caching;

/// <summary>
/// Counts what the cache actually did, so "hit rate" is a measurement rather than a claim.
/// </summary>
/// <remarks>
/// <para>
/// HybridCache does not expose hit/miss counters, and it cannot: a "hit" may be served from L1
/// (in-memory) or L2 (Redis), and from the caller's side both look like the factory simply not
/// running. So the miss is what gets counted — <b>the factory delegate only executes on a
/// miss</b> — and hits are inferred as reads minus factory invocations.
/// </para>
/// <para>
/// That inference is exactly what makes stampede protection measurable. Fire 200 concurrent
/// requests at a cold key: 200 reads, and if the factory ran once, 199 of them were coalesced
/// onto a single database query rather than each issuing their own.
/// </para>
/// </remarks>
public sealed class CacheMetrics
{
    private long _reads;
    private long _factoryInvocations;
    private long _invalidations;

    /// <summary>Every call into the cached read path, hit or miss.</summary>
    public void RecordRead() => Interlocked.Increment(ref _reads);

    /// <summary>The factory ran — meaning neither L1 nor L2 had the value.</summary>
    public void RecordFactoryInvocation() => Interlocked.Increment(ref _factoryInvocations);

    public void RecordInvalidation() => Interlocked.Increment(ref _invalidations);

    public long Reads => Interlocked.Read(ref _reads);
    public long FactoryInvocations => Interlocked.Read(ref _factoryInvocations);
    public long Invalidations => Interlocked.Read(ref _invalidations);

    /// <summary>Reads that did not run the factory.</summary>
    public long Hits => Math.Max(0, Reads - FactoryInvocations);

    public double HitRatePercent => Reads == 0 ? 0 : Math.Round(100.0 * Hits / Reads, 2);

    public void Reset()
    {
        Interlocked.Exchange(ref _reads, 0);
        Interlocked.Exchange(ref _factoryInvocations, 0);
        Interlocked.Exchange(ref _invalidations, 0);
    }
}
