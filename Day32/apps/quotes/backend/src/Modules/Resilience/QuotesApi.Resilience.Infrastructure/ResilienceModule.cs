using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace QuotesApi.Resilience.Infrastructure;

/// <summary>
/// The Polly pipeline around the outbound dependency: bulkhead, total timeout, idempotent-only
/// retry, circuit breaker and attempt timeout, plus the state provider and event log that make
/// the breaker's closed to open to half-open cycle observable.
/// </summary>
/// <remarks>
/// The whole of this module's HTTP surface is dev-only. <c>POST /breaker/{action}</c> and the
/// fault injectors are levers over the application's behaviour, and the fake upstream exists
/// purely so the pipeline has something to call. None of it is mapped in Production.
/// </remarks>
public static class ResilienceModule
{
    public static IServiceCollection AddResilienceModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddUpstreamResilience(configuration);
        return services;
    }

    /// <summary>Dev-only, and named so the host cannot map it by accident.</summary>
    public static void MapResilienceDevEndpoints(this WebApplication app)
    {
        app.MapResilienceEndpoints();
    }
}
