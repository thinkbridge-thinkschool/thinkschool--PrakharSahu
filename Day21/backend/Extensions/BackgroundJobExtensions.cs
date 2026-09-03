using QuotesApi.Hosted;
using QuotesApi.Jobs;
using QuotesApi.Jobs.Handlers;

namespace QuotesApi.Extensions;

public static class BackgroundJobExtensions
{
    /// <summary>Configuration section holding <see cref="JobQueueOptions"/>.</summary>
    public const string ConfigurationSection = "BackgroundJobs";

    public static IServiceCollection AddBackgroundJobs(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<JobQueueOptions>(configuration.GetSection(ConfigurationSection));

        // Singletons, and they have to be. The queue and the store are the shared state that
        // request threads write to and the worker reads from — a scoped registration would
        // give every request its own empty queue, and jobs would vanish silently.
        services.AddSingleton<IJobQueue, ChannelJobQueue>();
        services.AddSingleton<IJobStore, InMemoryJobStore>();

        // Handlers are SCOPED, because they depend on scoped services (IQuoteRepository, and
        // through it AppDbContext). JobProcessor resolves them from a per-job scope; see the
        // captive-dependency note in QuoteReportHandler.
        services.AddScoped<IJobHandler, QuoteReportHandler>();
        services.AddScoped<IJobHandler, SimulatedWorkHandler>();

        // Registration order is stop order, reversed. JobPipelineDiagnostics is registered
        // first so it stops LAST, which is what lets its StopAsync report on a queue the
        // processor has already finished draining.
        services.AddHostedService<JobPipelineDiagnostics>();
        services.AddHostedService<JobProcessor>();

        return services;
    }

    /// <summary>
    /// Gives the host longer to stop than a job is given to finish.
    /// </summary>
    /// <remarks>
    /// The default <c>ShutdownTimeout</c> is 5 seconds, which is shorter than the default
    /// 10-second grace period a running job gets. Left alone, the host would stop waiting and
    /// kill the process while the job was still inside its grace window — the grace period
    /// would delay the kill and prevent nothing. Deriving one from the other here means the
    /// two cannot drift apart when either is tuned.
    /// </remarks>
    public static IServiceCollection AddJobAwareShutdownTimeout(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var grace = configuration
            .GetSection(ConfigurationSection)
            .GetValue<TimeSpan?>(nameof(JobQueueOptions.ShutdownGrace))
            ?? TimeSpan.FromSeconds(10);

        // Margin covers draining the rest of the queue and the other hosted services stopping.
        services.Configure<HostOptions>(options =>
            options.ShutdownTimeout = grace + TimeSpan.FromSeconds(10));

        return services;
    }
}
