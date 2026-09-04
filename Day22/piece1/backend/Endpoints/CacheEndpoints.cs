using QuotesApi.Caching;
using QuotesApi.Data;

namespace QuotesApi.Endpoints;

public sealed record CacheModeRequest(bool Enabled);

public static class CacheEndpoints
{
    public static void MapCacheEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/cache");

        // ---------------------------------------------------------------------------------
        // GET /api/cache/stats — the measurement surface.
        //
        // dbQueries is the number that settles the argument. Requests and database queries are
        // the same number without a cache; the entire claim of a cache is that they stop being
        // the same number, and this endpoint is where that is read off.
        // ---------------------------------------------------------------------------------
        group.MapGet("/stats", (CacheMetrics metrics, DbQueryCounter dbCounter, CacheOptions options) =>
            Results.Ok(new
            {
                cacheEnabled = options.Enabled,
                layers = options.Layers,
                redisConnected = options.RedisConnected,

                reads = metrics.Reads,
                hits = metrics.Hits,
                misses = metrics.FactoryInvocations,
                hitRatePercent = metrics.HitRatePercent,
                invalidations = metrics.Invalidations,

                dbQueries = dbCounter.Commands,

                expiration = options.Expiration.ToString(),
                localExpiration = options.LocalExpiration.ToString(),
                simulatedQueryCostMs = options.SimulatedQueryCost.TotalMilliseconds,

                note = "misses == factory invocations. Under stampede protection, N concurrent "
                     + "readers of one cold key produce exactly ONE miss."
            }));

        // POST /api/cache/reset — zero the counters and drop the cached entry.
        //
        // Every load-test arm starts from here, so a run measures itself rather than whatever
        // the previous run left warm.
        group.MapPost("/reset", async (
            CacheMetrics metrics, DbQueryCounter dbCounter, IQuoteReader reader,
            CancellationToken ct) =>
        {
            await reader.InvalidateAsync(ct);
            metrics.Reset();
            dbCounter.Reset();

            // Invalidate itself counts a read and a DB write on some paths; zeroing afterwards
            // means the caller starts from a genuine zero rather than from one.
            metrics.Reset();
            dbCounter.Reset();

            return Results.Ok(new { reset = true });
        });

        // ---------------------------------------------------------------------------------
        // POST /api/cache/mode — switch caching off and on at runtime.
        //
        // This is what makes the before/after honest. Both arms run the same binary, hit the
        // same URL and execute the same code path up to one branch. Restarting with different
        // configuration would also change JIT warmth, connection-pool state and page cache, and
        // the delta would no longer belong to the cache.
        //
        // Development only, for the obvious reason.
        // ---------------------------------------------------------------------------------
        group.MapPost("/mode", (
            CacheModeRequest request,
            CacheOptions options,
            IWebHostEnvironment environment,
            ILogger<Program> logger) =>
        {
            if (!environment.IsDevelopment())
            {
                return Results.NotFound();
            }

            options.Enabled = request.Enabled;
            logger.LogWarning("Cache switched {State}.", request.Enabled ? "ON" : "OFF");

            return Results.Ok(new { cacheEnabled = options.Enabled });
        });

        // POST /api/cache/invalidate — drop the entry without touching the counters.
        group.MapPost("/invalidate", async (IQuoteReader reader, CancellationToken ct) =>
        {
            await reader.InvalidateAsync(ct);
            return Results.Ok(new { invalidated = CachedQuoteReader.ListCacheKey });
        });
    }
}
