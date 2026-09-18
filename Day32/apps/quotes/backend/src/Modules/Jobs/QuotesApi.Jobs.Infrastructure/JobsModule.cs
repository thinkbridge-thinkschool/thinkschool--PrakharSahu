using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace QuotesApi.Jobs.Infrastructure;

/// <summary>
/// Background work execution: the queue, the store, the processor loop and the HTTP surface
/// for enqueuing and inspecting jobs.
/// </summary>
/// <remarks>
/// <para>
/// This module knows how to <em>run</em> work. It does not know what any particular job does.
/// <c>QuoteReportHandler</c> used to be registered here, which meant the Jobs module referenced
/// the quote repository and could not be reasoned about without reading the Quotes module too.
/// It now lives in <c>QuotesApi.Quotes.Infrastructure</c> and arrives through the
/// <c>IJobHandler</c> port this module publishes in its Contracts.
/// </para>
/// <para>
/// The only handler left here is <c>SimulatedWorkHandler</c>, which genuinely belongs: it
/// exercises the pipeline itself and depends on nothing outside it.
/// </para>
/// </remarks>
public static class JobsModule
{
    public static IServiceCollection AddJobsModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddBackgroundJobs(configuration);
        services.AddJobAwareShutdownTimeout(configuration);
        return services;
    }

    public static IEndpointRouteBuilder MapJobsModule(this IEndpointRouteBuilder app)
    {
        app.MapJobEndpoints();
        return app;
    }
}
