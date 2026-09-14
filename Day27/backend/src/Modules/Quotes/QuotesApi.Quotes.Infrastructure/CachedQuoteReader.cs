using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using QuotesApi.Quotes.Application;
using QuotesApi.Quotes.Domain;

namespace QuotesApi.Quotes.Infrastructure;

/// <summary>
/// Serves the quote list through <see cref="HybridCache"/>, with stampede protection.
/// </summary>
/// <remarks>
/// <para><b>What HybridCache is.</b> A two-level cache: L1 in-memory inside the process, L2 a
/// distributed <c>IDistributedCache</c> (Redis here). A read checks L1, then L2, then the
/// factory; a fill populates both on the way back. One API in place of the hand-rolled
/// "check memory, check Redis, take a lock, re-check, query, write both back" that almost
/// everybody writes slightly wrong.</para>
///
/// <para><b>What stampede protection is, and why it is the whole point.</b> When a hot key
/// expires under load, every in-flight request misses at the same instant and each one queries
/// the database — N identical queries for one value. That is a cache stampede, and it is worst
/// exactly when the system is busiest, because the number of duplicate queries scales with
/// traffic. It is also self-reinforcing: the duplicate queries slow the database, which widens
/// the window, which admits more duplicates.</para>
///
/// <para>HybridCache fixes it by <b>coalescing concurrent callers of the same key onto a single
/// factory invocation</b>. The first caller runs the factory; the rest await its result. Two
/// hundred concurrent requests against a cold key produce one database query, not two hundred.
/// Nothing in this class implements that — it is a property of calling
/// <c>GetOrCreateAsync</c>, and it is the reason to reach for HybridCache rather than
/// <c>IDistributedCache</c> directly.</para>
///
/// <para><b>Why the factory takes state.</b> The <c>(reader, ct)</c> overload passes state
/// explicitly instead of capturing <c>this</c> in a closure, so the delegate can be
/// <c>static</c> and cached once rather than allocated per request — which matters on the hot
/// path this class exists to make fast.</para>
/// </remarks>
public sealed class CachedQuoteReader : IQuoteReader
{
    /// <summary>
    /// One key for the whole list.
    /// </summary>
    /// <remarks>
    /// The list is served whole and invalidated whole, so per-quote keys would buy nothing and
    /// cost a fan-out read per request. The <c>v1</c> suffix is a manual schema version: change
    /// the cached shape and bump it, or a rolling deploy will have new code reading old entries
    /// out of Redis. The tag exists so a future per-quote cache can be dropped alongside this
    /// one in a single call.
    /// </remarks>
    public const string ListCacheKey = "quotes:all:v1";
    public const string QuotesTag = "quotes";

    private readonly HybridCache _cache;
    private readonly IQuoteRepository _repository;
    private readonly CacheMetrics _metrics;
    private readonly CacheOptions _options;
    private readonly ILogger<CachedQuoteReader> _logger;

    public CachedQuoteReader(
        HybridCache cache,
        IQuoteRepository repository,
        CacheMetrics metrics,
        CacheOptions options,
        ILogger<CachedQuoteReader> logger)
    {
        _cache = cache;
        _repository = repository;
        _metrics = metrics;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CachedQuote>> GetAllAsync(CancellationToken cancellationToken)
    {
        _metrics.RecordRead();

        // The load test's "before" arm. Same endpoint, same request, same binary — only this
        // flag differs, so the two measurements are comparable in a way that hitting two
        // different URLs would not be.
        if (!_options.Enabled)
        {
            return await LoadFromDatabaseAsync(cancellationToken);
        }

        return await _cache.GetOrCreateAsync(
            ListCacheKey,
            state: this,
            factory: static (reader, ct) =>
                new ValueTask<IReadOnlyList<CachedQuote>>(reader.LoadFromDatabaseAsync(ct)),
            options: new HybridCacheEntryOptions
            {
                Expiration = _options.Expiration,
                LocalCacheExpiration = _options.LocalExpiration
            },
            tags: new[] { QuotesTag },
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The expensive path. Runs <b>once per stampede</b>, not once per caller.
    /// </summary>
    private async Task<IReadOnlyList<CachedQuote>> LoadFromDatabaseAsync(
        CancellationToken cancellationToken)
    {
        _metrics.RecordFactoryInvocation();

        var quotes = await _repository.GetAllAsync(cancellationToken);
        var projected = quotes.Select(CachedQuote.From).ToList();

        if (_options.SimulatedQueryCost > TimeSpan.Zero)
        {
            await Task.Delay(_options.SimulatedQueryCost, cancellationToken);
        }

        _logger.LogInformation(
            "CACHE MISS: loaded {Count} quotes from the database (factory invocation #{Invocation}).",
            projected.Count, _metrics.FactoryInvocations);

        return projected;
    }

    public async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        _metrics.RecordInvalidation();

        // Removes from L1 and L2 together. Dropping only Redis would leave every instance
        // serving its own stale in-memory copy until L1 expired — the classic
        // "I cleared the cache and nothing changed" bug.
        await _cache.RemoveAsync(ListCacheKey, cancellationToken);

        _logger.LogInformation("Cache invalidated for key {Key}.", ListCacheKey);
    }
}
