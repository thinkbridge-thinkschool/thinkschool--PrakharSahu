using Microsoft.Extensions.Caching.Hybrid;
using StackExchange.Redis;
using QuotesApi.Quotes.Application;
using QuotesApi.Quotes.Infrastructure;
using QuotesApi.Persistence;

namespace QuotesApi.Quotes.Infrastructure;

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

        // Which identity to present, when the endpoint is reached with a token rather than a key.
        // Empty off Azure, where there is no managed identity to ask for.
        var redisClientId = configuration["AZURE_CLIENT_ID"];

        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            // -------------------------------------------------------------------------------
            // Two ways to reach Redis, chosen by whether an identity was supplied.
            //
            // The key-based path is one line: hand StackExchange.Redis a connection string with
            // a password in it. It is also the reason most deployments that claim to hold no
            // secrets hold exactly one, and it would have been the second secret in this one.
            //
            // The token path asks the platform for an Entra token instead, exactly as the SQL
            // driver and the Service Bus client already do. It needs a real ConnectionMultiplexer
            // rather than a configuration string, because the token has to be refreshed —
            // ConfigureForAzureWithUserAssignedManagedIdentityAsync installs a handler that
            // re-authenticates before expiry and on reconnect. A token pasted in as a password
            // would work for about an hour and then fail in a way that looks like a network
            // fault.
            //
            // MULTIPLEXER, NOT CONNECTION. StackExchange.Redis multiplexes every operation over
            // one connection and is built to be shared for the lifetime of the process; the
            // factory below is invoked once. Creating one per operation is the standard way to
            // exhaust a connection pool and conclude that Redis is slow.
            // -------------------------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(redisClientId))
            {
                services.AddStackExchangeRedisCache(redis =>
                {
                    redis.InstanceName = "quotes:";
                    redis.ConnectionMultiplexerFactory = async () =>
                    {
                        var configurationOptions = await ConfigurationOptions
                            .Parse(redisConnection)
                            .ConfigureForAzureWithUserAssignedManagedIdentityAsync(redisClientId);

                        // The cache refuses the non-TLS port, so this is not optional; it is
                        // stated rather than inherited so a future edit cannot quietly drop it.
                        configurationOptions.Ssl = true;

                        // A cold Basic-tier cache and a cold container start at the same time.
                        // The default 5s is enough for a warm process and not for this one.
                        configurationOptions.ConnectTimeout = 15_000;
                        configurationOptions.AbortOnConnectFail = false;

                        return await ConnectionMultiplexer.ConnectAsync(configurationOptions);
                    };
                });

                options.RedisConnected = true;
                bootstrapLogger?.LogInformation(
                    "Cache L2 enabled: Redis at {Redis}, authenticated with managed identity {ClientId}.",
                    redisConnection, redisClientId);
            }
            else
            {
                // Kept for local development against a plain `docker run redis`, where there is
                // no identity to present and no token to get.
                services.AddStackExchangeRedisCache(redis =>
                {
                    redis.Configuration = redisConnection;
                    redis.InstanceName = "quotes:";
                });

                options.RedisConnected = true;
                bootstrapLogger?.LogInformation(
                    "Cache L2 enabled: Redis at {Redis} (no AZURE_CLIENT_ID, so key or no auth).",
                    redisConnection);
            }
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
