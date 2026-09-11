using System.Diagnostics;
using Microsoft.Extensions.Options;
using QuotesApi.Models;
using QuotesApi.Services;

namespace QuotesApi.Jobs;

/// <summary>
/// Drains the job queue on a single background loop, one job at a time.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole point of Day 18: the request thread does not do slow work. An endpoint
/// enqueues, answers <c>202 Accepted</c> with a job id, and returns. Everything expensive
/// happens here, off the request path, where taking thirty seconds inconveniences nobody.
/// </para>
///
/// <para><b>How it shuts down cleanly</b></para>
/// <para>
/// Three things happen in order, and the order is the design:
/// </para>
/// <list type="number">
///   <item>
///     <b>The front door closes first.</b> <see cref="StopAsync"/> calls
///     <see cref="IJobQueue.Complete"/> <em>before</em> delegating to the base class, so no
///     new job can be accepted while the queue is being drained. Do this in the other order
///     and a request that lands mid-shutdown gets a 202 for work nothing will ever run.
///   </item>
///   <item>
///     <b>The job already running gets a grace period.</b> When <c>stoppingToken</c> fires,
///     the running job is not cancelled instantly — it is given
///     <see cref="JobQueueOptions.ShutdownGrace"/> to finish on its own, and only then
///     cancelled. Killing a half-written job instantly is not graceful, it is just fast.
///   </item>
///   <item>
///     <b>Everything still queued is marked Cancelled, not run.</b> Draining a hundred queued
///     jobs during shutdown would hold the process open far past any sane timeout. They are
///     drained without executing and marked <see cref="JobStatus.Cancelled"/>, so a caller
///     polling for one is told the truth instead of polling a job id forever.
///   </item>
/// </list>
/// <para>
/// The bound on all of it is <c>HostOptions.ShutdownTimeout</c>, configured in
/// <c>Program.cs</c> to comfortably exceed the grace period. If <see cref="ExecuteAsync"/>
/// has not returned by then the host stops waiting and the process exits mid-job — which is
/// exactly the ungraceful outcome the grace period exists to avoid, and why every handler is
/// required to observe its token.
/// </para>
/// </remarks>
public sealed class JobProcessor : BackgroundService
{
    private readonly IJobQueue _queue;
    private readonly IJobStore _store;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly JobQueueOptions _options;
    private readonly ILogger<JobProcessor> _logger;

    public JobProcessor(
        IJobQueue queue,
        IJobStore store,
        IServiceScopeFactory scopeFactory,
        IClock clock,
        IOptions<JobQueueOptions> options,
        ILogger<JobProcessor> logger)
    {
        _queue = queue;
        _store = store;
        _scopeFactory = scopeFactory;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Job processor started. Capacity {Capacity}, shutdown grace {Grace}.",
            _options.Capacity, _options.ShutdownGrace);

        try
        {
            // CancellationToken.None, deliberately, and this is the subtle part.
            //
            // Passing stoppingToken here would make the loop throw the moment shutdown
            // begins, abandoning both the running job and everything queued behind it. The
            // loop is instead ended by StopAsync closing the channel: ReadAllAsync yields
            // what is left and then completes on its own. Shutdown is expressed by closing
            // the queue, not by tearing the reader down.
            await foreach (var job in _queue.DequeueAllAsync(CancellationToken.None))
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    // Shutdown began while this one was still waiting its turn. Drain it
                    // without running it, so the loop empties quickly and whoever is polling
                    // learns it will never run.
                    MarkCancelled(job, "The host shut down before this job started.");
                    continue;
                }

                await ProcessOneAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown. Not an error, and not worth a stack trace in the logs.
        }
        catch (Exception failure)
        {
            // Nothing may escape ExecuteAsync. Since .NET 6 the default
            // BackgroundServiceExceptionBehavior is StopHost, so an unhandled exception here
            // takes the entire web application down — one malformed job would stop the API
            // serving quotes. Individual job failures are already caught in ProcessOneAsync;
            // reaching this handler means the loop itself broke.
            _logger.LogCritical(failure, "The job processor loop failed. No further jobs will run.");
        }

        _logger.LogInformation("Job processor stopped. {Remaining} job(s) left unprocessed.", _queue.Count);
    }

    private async Task ProcessOneAsync(Job job, CancellationToken stoppingToken)
    {
        // Per-job cancellation source. Registered with the store so DELETE /api/jobs/{id}
        // can cancel this specific job without touching any other.
        using var jobCts = new CancellationTokenSource();

        // Shutdown does not cancel the running job immediately — it starts a countdown.
        // The job gets ShutdownGrace to finish on its own before the token is signalled.
        using var shutdownRegistration = stoppingToken.Register(() =>
        {
            _logger.LogInformation(
                "Shutdown requested while job {JobId} was running. Allowing {Grace} to finish.",
                job.Id, _options.ShutdownGrace);
            try { jobCts.CancelAfter(_options.ShutdownGrace); }
            catch (ObjectDisposedException) { /* the job finished first; nothing to cancel */ }
        });

        _store.RegisterRunning(job.Id, jobCts);

        job.Status = JobStatus.Running;
        job.StartedAt = _clock.UtcNow;

        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation(
            "Job {JobId} ({JobType}) started after {LatencyMs}ms in the queue.",
            job.Id, job.Type, job.QueueLatency?.TotalMilliseconds ?? 0);

        try
        {
            // A scope per job. The processor is a singleton, so it cannot hold AppDbContext
            // or anything else scoped; resolving the handler from a fresh scope is what lets
            // handlers depend on scoped services safely, and disposes them per job rather
            // than per process.
            using var scope = _scopeFactory.CreateScope();

            var handler = scope.ServiceProvider
                .GetServices<IJobHandler>()
                .FirstOrDefault(h => string.Equals(h.JobType, job.Type, StringComparison.OrdinalIgnoreCase));

            if (handler is null)
            {
                throw new InvalidOperationException($"No handler is registered for job type '{job.Type}'.");
            }

            job.Result = await handler.HandleAsync(job, jobCts.Token);
            job.Status = JobStatus.Succeeded;

            _logger.LogInformation(
                "Job {JobId} succeeded in {ElapsedMs}ms.", job.Id, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // Cancelled, not failed — and the two are worth distinguishing to whoever polls.
            // Which kind of cancellation it was also matters: a caller's DELETE is a decision,
            // a shutdown is an accident of timing and the job is worth resubmitting.
            job.Status = JobStatus.Cancelled;
            job.Error = stoppingToken.IsCancellationRequested
                ? "The host shut down before this job finished."
                : "The job was cancelled.";

            _logger.LogWarning(
                "Job {JobId} cancelled after {ElapsedMs}ms ({Reason}).",
                job.Id, stopwatch.ElapsedMilliseconds,
                stoppingToken.IsCancellationRequested ? "host shutdown" : "caller request");
        }
        catch (Exception failure)
        {
            job.Status = JobStatus.Failed;
            // The message only. An exception's ToString names internal types and file paths,
            // and this field is served over HTTP.
            job.Error = failure.Message;

            _logger.LogError(
                failure, "Job {JobId} ({JobType}) failed after {ElapsedMs}ms.",
                job.Id, job.Type, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            job.CompletedAt = _clock.UtcNow;
            _store.ReleaseRunning(job.Id);
        }
    }

    private void MarkCancelled(Job job, string reason)
    {
        job.Status = JobStatus.Cancelled;
        job.Error = reason;
        job.CompletedAt = _clock.UtcNow;
        _logger.LogWarning("Job {JobId} ({JobType}) drained without running: {Reason}", job.Id, job.Type, reason);
    }

    /// <summary>
    /// Closes the queue, then lets the base class signal <c>stoppingToken</c> and wait for
    /// <see cref="ExecuteAsync"/> to return.
    /// </summary>
    /// <remarks>
    /// The ordering is load-bearing. <c>base.StopAsync</c> is what signals the token and
    /// awaits the loop; closing the queue first means that by the time the loop is asked to
    /// wind down, nothing new can arrive behind it. Reversing these two lines produces a
    /// shutdown that races incoming requests, and the failure only shows up under load.
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Job processor stopping. Closing the queue to new work.");
        _queue.Complete();

        await base.StopAsync(cancellationToken);
    }
}
