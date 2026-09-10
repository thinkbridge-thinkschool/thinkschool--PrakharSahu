using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuotesApi.Jobs;
using QuotesApi.Models;
using QuotesApi.Services;

// This file lives under QuotesApi.*, and the API has its own QuotesApi.Options namespace for
// JwtOptions. Inside a QuotesApi.* namespace that one wins over Microsoft.Extensions.Options,
// so the bare `Options.Create` resolves to a namespace rather than the static class and fails
// with "the type or namespace 'Create' does not exist". The alias removes the ambiguity.
using MsOptions = Microsoft.Extensions.Options.Options;

namespace QuotesApi.Jobs.Tests;

/// <summary>
/// Exercises the worker directly rather than through the web host.
/// </summary>
/// <remarks>
/// The graceful-shutdown behaviour is the deliverable, and it is a race by nature: a job in
/// flight, a token signalled, a grace period, a host waiting. Driving <see cref="JobProcessor"/>
/// straight makes those transitions deterministic, which a test that boots Kestrel and sends
/// a real SIGTERM never is — especially on Windows, where there is no SIGTERM to send.
/// </remarks>
public sealed class JobProcessorTests
{
    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    /// <summary>A handler whose behaviour each test dictates.</summary>
    private sealed class ProbeHandler : IJobHandler
    {
        private readonly Func<Job, CancellationToken, Task<string>> _body;
        public ProbeHandler(Func<Job, CancellationToken, Task<string>> body) => _body = body;
        public string JobType => "probe";
        public Task<string> HandleAsync(Job job, CancellationToken ct) => _body(job, ct);
    }

    private static (JobProcessor Processor, IJobQueue Queue, IJobStore Store) Build(
        Func<Job, CancellationToken, Task<string>> handlerBody,
        TimeSpan? shutdownGrace = null,
        int capacity = 100)
    {
        var options = MsOptions.Create(new JobQueueOptions
        {
            Capacity = capacity,
            ShutdownGrace = shutdownGrace ?? TimeSpan.FromSeconds(10)
        });

        var queue = new ChannelJobQueue(options, NullLogger<ChannelJobQueue>.Instance);
        var store = new InMemoryJobStore();

        var services = new ServiceCollection();
        services.AddScoped<IJobHandler>(_ => new ProbeHandler(handlerBody));
        var provider = services.BuildServiceProvider();

        var processor = new JobProcessor(
            queue, store,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestClock(), options,
            NullLogger<JobProcessor>.Instance);

        return (processor, queue, store);
    }

    private static Job NewJob(string? payload = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = "probe",
        Payload = payload,
        CreatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>Polls until the condition holds, so tests never depend on a fixed sleep.</summary>
    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task Successful_job_runs_off_the_calling_thread_and_records_its_result()
    {
        var (processor, queue, store) = Build((_, _) => Task.FromResult("done"));
        await processor.StartAsync(CancellationToken.None);

        var job = NewJob();
        store.Add(job);
        Assert.True(await queue.EnqueueAsync(job, CancellationToken.None));

        // Enqueue returns before the work happens — that is the point of the pattern.
        Assert.True(await WaitUntil(() => job.Status == JobStatus.Succeeded, TimeSpan.FromSeconds(5)));
        Assert.Equal("done", job.Result);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.CompletedAt);
        Assert.Null(job.Error);

        await processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_throwing_handler_fails_only_its_own_job_and_the_worker_keeps_going()
    {
        var shouldThrow = true;
        var (processor, queue, store) = Build((_, _) =>
            shouldThrow
                ? throw new InvalidOperationException("boom")
                : Task.FromResult("second"));

        await processor.StartAsync(CancellationToken.None);

        var failing = NewJob();
        store.Add(failing);
        await queue.EnqueueAsync(failing, CancellationToken.None);
        Assert.True(await WaitUntil(() => failing.Status == JobStatus.Failed, TimeSpan.FromSeconds(5)));
        Assert.Equal("boom", failing.Error);

        // The loop must survive a handler exception. If it did not, the host would stop —
        // BackgroundServiceExceptionBehavior.StopHost is the default since .NET 6.
        shouldThrow = false;
        var second = NewJob();
        store.Add(second);
        await queue.EnqueueAsync(second, CancellationToken.None);
        Assert.True(await WaitUntil(() => second.Status == JobStatus.Succeeded, TimeSpan.FromSeconds(5)));

        await processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cancelling_a_running_job_marks_it_Cancelled_not_Failed()
    {
        var started = new TaskCompletionSource();
        var (processor, queue, store) = Build(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        });

        await processor.StartAsync(CancellationToken.None);

        var job = NewJob();
        store.Add(job);
        await queue.EnqueueAsync(job, CancellationToken.None);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(store.TryRequestCancellation(job.Id));

        Assert.True(await WaitUntil(() => job.Status == JobStatus.Cancelled, TimeSpan.FromSeconds(5)));
        // Cancelled, and attributed to the caller rather than to shutdown.
        Assert.Equal("The job was cancelled.", job.Error);

        await processor.StopAsync(CancellationToken.None);
    }

    // =====================================================================================
    // Graceful shutdown — the deliverable.
    // =====================================================================================

    [Fact]
    public async Task Shutdown_lets_an_in_flight_job_finish_inside_its_grace_period()
    {
        var started = new TaskCompletionSource();
        var (processor, queue, store) = Build(
            async (_, ct) =>
            {
                started.TrySetResult();
                // Comfortably shorter than the grace period below.
                await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
                return "finished during shutdown";
            },
            shutdownGrace: TimeSpan.FromSeconds(5));

        await processor.StartAsync(CancellationToken.None);

        var job = NewJob();
        store.Add(job);
        await queue.EnqueueAsync(job, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await processor.StopAsync(CancellationToken.None);

        // The grace period is the difference between graceful and merely fast: the job was
        // mid-flight when shutdown began and still completed successfully.
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal("finished during shutdown", job.Result);
    }

    [Fact]
    public async Task A_job_that_outlasts_the_grace_period_is_cancelled_and_blamed_on_shutdown()
    {
        var started = new TaskCompletionSource();
        var (processor, queue, store) = Build(
            async (_, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return "never";
            },
            shutdownGrace: TimeSpan.FromMilliseconds(300));

        await processor.StartAsync(CancellationToken.None);

        var job = NewJob();
        store.Add(job);
        await queue.EnqueueAsync(job, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await processor.StopAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(JobStatus.Cancelled, job.Status);
        // Distinguished from a caller's cancellation, because only one of the two is worth
        // resubmitting automatically.
        Assert.Equal("The host shut down before this job finished.", job.Error);

        // It waited for the grace period, then stopped — it did not hang.
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 200, 5000);
    }

    [Fact]
    public async Task Jobs_still_queued_at_shutdown_are_drained_without_running()
    {
        var executions = 0;
        var firstStarted = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var (processor, queue, store) = Build(
            async (_, _) =>
            {
                if (Interlocked.Increment(ref executions) == 1)
                {
                    firstStarted.TrySetResult();
                    await release.Task;
                }
                return "ran";
            },
            shutdownGrace: TimeSpan.FromMilliseconds(200));

        await processor.StartAsync(CancellationToken.None);

        var running = NewJob();
        store.Add(running);
        await queue.EnqueueAsync(running, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Three more pile up behind the one that is blocked.
        var queued = Enumerable.Range(0, 3).Select(_ => NewJob()).ToList();
        foreach (var job in queued)
        {
            store.Add(job);
            await queue.EnqueueAsync(job, CancellationToken.None);
        }

        var stop = processor.StopAsync(CancellationToken.None);
        release.TrySetResult();
        await stop;

        // Running them all would hold the process open for as long as the backlog takes,
        // which is the opposite of a graceful shutdown. They are drained and reported instead.
        Assert.All(queued, job => Assert.Equal(JobStatus.Cancelled, job.Status));
        Assert.All(queued, job =>
            Assert.Equal("The host shut down before this job started.", job.Error));
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task The_queue_refuses_new_work_once_shutdown_has_started()
    {
        var (processor, queue, store) = Build((_, _) => Task.FromResult("done"));
        await processor.StartAsync(CancellationToken.None);
        await processor.StopAsync(CancellationToken.None);

        // The endpoint turns this false into a 503 rather than a 202 for work that will never
        // run. Closing the queue before awaiting the loop is what makes it observable here.
        Assert.False(await queue.EnqueueAsync(NewJob(), CancellationToken.None));
    }

    [Fact]
    public async Task A_full_queue_applies_backpressure_rather_than_dropping_jobs()
    {
        var release = new TaskCompletionSource();
        var (processor, queue, store) = Build(async (_, _) => { await release.Task; return "done"; }, capacity: 2);
        await processor.StartAsync(CancellationToken.None);

        // One is pulled out and blocks in the handler; two more fill the channel.
        for (var i = 0; i < 3; i++)
        {
            var job = NewJob();
            store.Add(job);
            await queue.EnqueueAsync(job, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        // The fourth has nowhere to go. BoundedChannelFullMode.Wait means it waits — it is
        // not silently dropped, which would leave the caller polling a job id forever.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await queue.EnqueueAsync(NewJob(), cts.Token));

        release.TrySetResult();
        await processor.StopAsync(CancellationToken.None);
    }
}
