using QuotesApi.Jobs;

namespace QuotesApi.Hosted;

/// <summary>
/// Verifies the job pipeline at startup and reports on it at shutdown.
/// </summary>
/// <remarks>
/// <para>
/// A plain <see cref="IHostedService"/>, not a <see cref="BackgroundService"/>, and the
/// difference is the reason this class exists alongside <see cref="JobProcessor"/>.
/// </para>
///
/// <para><b>IHostedService is two events. BackgroundService is a loop.</b></para>
/// <list type="bullet">
///   <item>
///     <see cref="StartAsync"/> runs <em>before the host starts serving requests</em> and the
///     host waits for it. That is precisely what you want for a fail-fast check — a missing
///     handler registration should stop the app from starting, not surface as a failed job at
///     three in the morning. It is also why nothing slow belongs here: every millisecond
///     spent in StartAsync is a millisecond the app is not accepting traffic, and a hang here
///     hangs startup completely.
///   </item>
///   <item>
///     <see cref="StopAsync"/> runs during shutdown, giving one clean place to report state.
///   </item>
///   <item>
///     There is no long-running work between the two. If there were, it would need its own
///     task and its own cancellation plumbing — which is all
///     <see cref="BackgroundService"/> actually is: an <see cref="IHostedService"/> whose
///     StartAsync kicks off <c>ExecuteAsync</c> and whose StopAsync signals a token and waits.
///   </item>
/// </list>
/// <para>
/// So the rule of thumb: <b>one-shot work at either end of the process lifetime →
/// IHostedService. Continuous work in between → BackgroundService.</b> Writing a
/// <c>while (true)</c> loop inside <c>IHostedService.StartAsync</c> is the classic mistake,
/// and it deadlocks startup, because the host is waiting for that method to return.
/// </para>
/// </remarks>
public sealed class JobPipelineDiagnostics : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IJobQueue _queue;
    private readonly ILogger<JobPipelineDiagnostics> _logger;

    /// <remarks>
    /// Takes <see cref="IServiceScopeFactory"/> rather than <c>IEnumerable&lt;IJobHandler&gt;</c>
    /// directly. Hosted services are singletons and handlers are scoped, so injecting them
    /// here is a captive dependency — and one the container refuses outright, because
    /// <c>ValidateScopes</c> is on by default in Development:
    /// <code>
    /// Cannot consume scoped service 'QuotesApi.Jobs.IJobHandler'
    /// from singleton 'Microsoft.Extensions.Hosting.IHostedService'.
    /// </code>
    /// Which is the container doing exactly its job: the alternative is one handler instance,
    /// holding one DbContext, shared by every job for the lifetime of the process.
    /// </remarks>
    public JobPipelineDiagnostics(
        IServiceScopeFactory scopeFactory,
        IJobQueue queue,
        ILogger<JobPipelineDiagnostics> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A scope just long enough to read the registrations. The handler instances are
        // discarded with the scope; only their declared types are of interest here.
        using var scope = _scopeFactory.CreateScope();
        var types = scope.ServiceProvider.GetServices<IJobHandler>().Select(h => h.JobType).ToList();

        if (types.Count == 0)
        {
            // Fails startup on purpose. An API that accepts jobs and has nothing to run them
            // would answer 202 for every request and complete none — a far worse failure than
            // refusing to boot, because it looks like it is working.
            throw new InvalidOperationException(
                "No IJobHandler implementations are registered. The job queue would accept work "
                + "that nothing can execute.");
        }

        var duplicates = types
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            // First-match-wins in the processor, so a duplicate means one handler silently
            // never runs. Better to say so at startup than to debug it from job results.
            throw new InvalidOperationException(
                $"Duplicate job handler types registered: {string.Join(", ", duplicates)}.");
        }

        _logger.LogInformation("Job pipeline ready. Handlers: {Handlers}.", string.Join(", ", types));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Ordering note: hosted services are stopped in reverse registration order, so this
        // runs after JobProcessor has already drained. A non-zero count here means the drain
        // did not finish within the shutdown timeout, which is worth seeing in the logs.
        var remaining = _queue.Count;

        if (remaining > 0)
        {
            _logger.LogWarning(
                "Shutting down with {Remaining} job(s) still queued. They were not run.", remaining);
        }
        else
        {
            _logger.LogInformation("Shutting down with an empty job queue.");
        }

        return Task.CompletedTask;
    }
}
