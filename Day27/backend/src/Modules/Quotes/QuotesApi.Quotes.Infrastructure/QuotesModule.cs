using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuotesApi.Jobs.Contracts;
using QuotesApi.Quotes.Application;

namespace QuotesApi.Quotes.Infrastructure;

/// <summary>
/// Everything the Quotes module contributes to the host, and the only thing the host is
/// allowed to call.
/// </summary>
/// <remarks>
/// <para>
/// The host composes modules; it does not know what is inside one. Before the split,
/// <c>Program.cs</c> named <c>QuoteRepository</c>, <c>IsOwnerHandler</c>,
/// <c>CachedQuoteReader</c> and <c>QuoteReportHandler</c> directly, so adding a service to the
/// quotes feature meant editing the composition root — and the composition root slowly became
/// the place that knew everything.
/// </para>
/// <para>
/// <b><see cref="QuoteReportHandler"/> is registered here, not by the Jobs module.</b> That is
/// the single clearest demonstration of why the boundary is real. The handler reads quotes, so
/// it needs <see cref="IQuoteRepository"/> — which lives in this module's Application layer and
/// which the Jobs module cannot see. Jobs publishes the <see cref="IJobHandler"/> port in its
/// Contracts; Quotes implements it and registers the implementation itself. Jobs never learns
/// that quotes exist.
/// </para>
/// </remarks>
public static class QuotesModule
{
    public static IServiceCollection AddQuotesModule(
        this IServiceCollection services,
        IConfiguration configuration,
        ILogger? bootstrapLogger = null)
    {
        services.AddScoped<IQuoteRepository, QuoteRepository>();

        // Ownership. The handler answers "is the caller the author of quote N", which needs the
        // repository, so the policy belongs to this module rather than to a global auth setup.
        services.AddScoped<IAuthorizationHandler, IsOwnerHandler>();
        services.AddAuthorization(options =>
        {
            options.AddPolicy("IsQuoteOwner", policy =>
                policy.Requirements.Add(new IsOwnerRequirement()));
        });

        // Day 21: HybridCache over the hot read, plus the counters that make hit rate and DB
        // load measurable. Runs L1-only when no Redis is configured.
        services.AddQuoteCaching(configuration, bootstrapLogger);

        // Day 18: this module's own background job, implementing the port Jobs publishes.
        services.AddScoped<IJobHandler, QuoteReportHandler>();

        return services;
    }

    /// <summary>The module's public HTTP surface.</summary>
    public static IEndpointRouteBuilder MapQuotesModule(this IEndpointRouteBuilder app)
    {
        app.MapQuoteEndpoints();
        return app;
    }

    /// <summary>
    /// Cache-control endpoints, mapped only in Development.
    /// </summary>
    /// <remarks>
    /// <c>POST /cache/mode</c> and <c>POST /cache/reset</c> are levers over the application's
    /// behaviour. Day 27 established that the right fix for a debug lever is absence rather than
    /// authentication: an endpoint that does not exist cannot be misconfigured. Keeping the
    /// dev-only surface in a separately named method means the host cannot map it by accident.
    /// </remarks>
    public static IEndpointRouteBuilder MapQuotesDevEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapCacheEndpoints();
        return app;
    }
}
