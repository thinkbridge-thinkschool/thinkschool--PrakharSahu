using Microsoft.Extensions.Caching.Hybrid;
using QuotesApi.Caching;
using QuotesApi.Data;

namespace QuotesApi.Extensions;

public static class CachingExtensions
{
    /// <summary>
    /// Wires HybridCache (L1 in-memory + optional L2 Redis) and the counters that make its
    /// effect measurable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Redis is optional by design, exactly as messaging is: with no connection string
    /// configured HybridCache still runs with L1 only, so the API and its tests work on a
    /// machine with no Redis anywhere near it. What changes without Redis is not correctness
    /// but blast radius — L1 is per-process, so a second replica has its own copy and a restart
    /// starts cold.
    /// </para>
    /// <para>
    /// HybridCache discovers L2 through the container: register an <c>IDistributedCache</c> and
    /// it is used automatically. There is no explicit "use Redis" call, which is convenient and
    /// worth knowing, because it also means a stray <c>IDistributedCache</c> registration
    /// silently becomes your L2.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddQuoteCaching(
        this IServiceCollection services,
        IConfiguration configuration,
        ILogger? bootstrapLogger = null)
    {
        var section = configuration.GetSection(CacheOptions.SectionName);

        // Bound once and registered as a singleton instance rather than through IOptions, so
        // the load-test endpoint can flip Enabled at runtime. See CacheOptions.
        var options = section.Get<CacheOptions>() ?? new CacheOptions();

        var redisConnection = configuration.GetConnectionString("Redis")
                              ?? section["RedisConnectionString"];

        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddStackExchangeRedisCache(redis =>
            {
                redis.Configuration = redisConnection;
                redis.InstanceName = "quotes:";
            });

            options.RedisConnected = true;
            bootstrapLogger?.LogInformation("Cache L2 enabled: Redis at {Redis}.", redisConnection);
        }
        else
        {
            options.RedisConnected = false;
            bootstrapLogger?.LogWarning(
                "No Redis configured (ConnectionStrings:Redis). HybridCache will run L1-only: "
                + "per-process, and cold after every restart.");
        }

        services.AddSingleton(options);
        services.AddSingleton<CacheMetrics>();
        // DbQueryCounter is registered by AddInfrastructure alongside the DbContext it counts,
        // so that the "before" arm still has a counter when caching is switched off.

        services.AddHybridCache(hybrid =>
        {
            hybrid.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = options.Expiration,
                LocalCacheExpiration = options.LocalExpiration
            };

            // Guard rails, not tuning. A cache that will accept anything is a memory leak with
            // a good reputation; these make an oversized entry fail loudly at development time
            // instead of quietly evicting everything useful in production.
            hybrid.MaximumPayloadBytes = 1024 * 1024;   // 1 MB
            hybrid.MaximumKeyLength = 512;
        });

        // Scoped: it depends on IQuoteRepository, which depends on AppDbContext.
        services.AddScoped<IQuoteReader, CachedQuoteReader>();

        return services;
    }
}
