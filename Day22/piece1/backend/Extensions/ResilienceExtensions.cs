using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Polly;
using Polly.CircuitBreaker;
using QuotesApi.Resilience;

namespace QuotesApi.Extensions;

/// <summary>
/// Day 22: wires the Polly pipeline, the typed client that uses it, and the observability that
/// makes the breaker's state machine visible.
/// </summary>
public static class ResilienceExtensions
{
    public static IServiceCollection AddUpstreamResilience(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new UpstreamOptions();
        configuration.GetSection(UpstreamOptions.SectionName).Bind(options);

        // A concrete singleton, not IOptions<T>. Nothing rebinds it at runtime, and the
        // pipeline factory below needs the values while it is building - before any scope
        // exists to resolve IOptions from.
        services.AddSingleton(options);

        services.AddSingleton<ResilienceEventLog>();
        services.AddSingleton<UpstreamFaults>();

        // Registered separately so endpoints can read the live circuit state. Polly writes into
        // it from inside the breaker; nothing else may construct a second one, or the endpoint
        // would report the state of a breaker that no traffic passes through.
        services.AddSingleton<CircuitBreakerStateProvider>();
        services.AddSingleton<CircuitBreakerManualControl>();

        // ---------------------------------------------------------------------------------
        // One named pipeline, resolved by key, shared by every caller.
        //
        // AddResiliencePipeline rather than HttpClient's AddResilienceHandler, for one reason
        // that matters on this day: the retry has to know whether the operation is idempotent,
        // and that fact has to survive outcomes with no response attached - a connection reset,
        // an attempt timeout. Carrying it on the ResilienceContext works for every outcome;
        // reading it off a response only works for the outcomes that have one.
        //
        // The build callback runs once. That single pipeline instance is what makes the breaker
        // and the bulkhead process-wide state rather than per-request objects that reset
        // constantly and never trip.
        // ---------------------------------------------------------------------------------
        services.AddResiliencePipeline<string, HttpResponseMessage>(
            UpstreamResilience.PipelineKey,
            (builder, context) => UpstreamResilience.Configure(
                builder,
                context.ServiceProvider.GetRequiredService<UpstreamOptions>(),
                context.ServiceProvider.GetRequiredService<ResilienceEventLog>(),
                context.ServiceProvider.GetRequiredService<CircuitBreakerStateProvider>(),
                context.ServiceProvider.GetRequiredService<CircuitBreakerManualControl>()));

        services.AddHttpClient<UpstreamClient>((serviceProvider, client) =>
        {
            client.BaseAddress = new Uri(ResolveBaseAddress(serviceProvider, options));

            // Infinite on purpose. Polly's attempt timeout is the only clock bounding a call.
            // Leaving HttpClient's own 100s default in place gives one condition two owners:
            // whichever fires first decides the exception type, and a pipeline tuned around
            // TimeoutRejectedException starts seeing TaskCanceledException instead the moment
            // the numbers cross.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        return services;
    }

    /// <summary>
    /// Where the outbound call should go.
    /// </summary>
    /// <remarks>
    /// Configuration wins if it is set - that is how this points at a real third party. With
    /// nothing configured it asks Kestrel which addresses it actually bound, so the demo works
    /// on whatever port the process ended up with instead of on the one the launch profile
    /// happens to name.
    /// </remarks>
    private static string ResolveBaseAddress(IServiceProvider serviceProvider, UpstreamOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.BaseAddress))
        {
            return options.BaseAddress;
        }

        var addresses = serviceProvider
            .GetService<IServer>()?
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;

        // Prefer plain HTTP: this is a loopback call to the same process, and the dev HTTPS
        // certificate is one more thing that can fail for reasons unrelated to resilience.
        var address =
            addresses?.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? addresses?.FirstOrDefault();

        // Kestrel binds to 0.0.0.0 / [::] when told to listen on any interface. Those are valid
        // bind targets and invalid connect targets, so they are rewritten to loopback.
        return address is null
            ? "http://localhost:5267"
            : address.Replace("//0.0.0.0", "//127.0.0.1").Replace("//[::]", "//127.0.0.1").Replace("//+", "//127.0.0.1");
    }
}
