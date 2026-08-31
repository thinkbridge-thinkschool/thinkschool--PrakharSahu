# Day 18 — Background jobs

> **Exercise:** Paste the BackgroundService + how it shuts down cleanly.
> One line: when Hangfire over a hosted service?

---

## The one-line answer

**Reach for Hangfire when the job must survive the process — scheduled, recurring, retried or
durable work — and stay with a hosted service when the work is in-process, fire-and-forget,
and losing it on restart is acceptable.**

---

## The BackgroundService

`backend/Jobs/JobProcessor.cs`. Comments trimmed here; the file carries the full reasoning.

```csharp
public sealed class JobProcessor : BackgroundService
{
    private readonly IJobQueue _queue;
    private readonly IJobStore _store;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly JobQueueOptions _options;
    private readonly ILogger<JobProcessor> _logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // CancellationToken.None, deliberately. Passing stoppingToken here would make the
            // loop throw the moment shutdown begins, abandoning both the running job and
            // everything queued behind it. The loop is ended by StopAsync closing the
            // channel: ReadAllAsync yields what is left, then completes on its own.
            await foreach (var job in _queue.DequeueAllAsync(CancellationToken.None))
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    // Shutdown began while this one was still waiting. Drain without running,
                    // so the loop empties fast and whoever is polling learns it never will.
                    MarkCancelled(job, "The host shut down before this job started.");
                    continue;
                }

                await ProcessOneAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception failure)
        {
            // Nothing may escape ExecuteAsync. Since .NET 6 the default
            // BackgroundServiceExceptionBehavior is StopHost, so an unhandled exception here
            // takes the whole web application down — one malformed job would stop the API
            // serving quotes.
            _logger.LogCritical(failure, "The job processor loop failed.");
        }
    }

    private async Task ProcessOneAsync(Job job, CancellationToken stoppingToken)
    {
        using var jobCts = new CancellationTokenSource();

        // Shutdown does not cancel the running job immediately — it starts a countdown.
        using var shutdownRegistration = stoppingToken.Register(() =>
        {
            try { jobCts.CancelAfter(_options.ShutdownGrace); }
            catch (ObjectDisposedException) { /* finished first */ }
        });

        _store.RegisterRunning(job.Id, jobCts);   // lets DELETE /api/jobs/{id} cancel it
        job.Status = JobStatus.Running;
        job.StartedAt = _clock.UtcNow;

        try
        {
            // A scope per job. The processor is a singleton, so it cannot hold AppDbContext
            // or anything else scoped; a fresh scope is what lets handlers depend on scoped
            // services safely.
            using var scope = _scopeFactory.CreateScope();

            var handler = scope.ServiceProvider.GetServices<IJobHandler>()
                .FirstOrDefault(h => string.Equals(h.JobType, job.Type, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No handler for job type '{job.Type}'.");

            job.Result = await handler.HandleAsync(job, jobCts.Token);
            job.Status = JobStatus.Succeeded;
        }
        catch (OperationCanceledException)
        {
            // Cancelled, not failed — and which kind matters. A caller's DELETE is a
            // decision; a shutdown is an accident of timing and worth resubmitting.
            job.Status = JobStatus.Cancelled;
            job.Error = stoppingToken.IsCancellationRequested
                ? "The host shut down before this job finished."
                : "The job was cancelled.";
        }
        catch (Exception failure)
        {
            job.Status = JobStatus.Failed;
            job.Error = failure.Message;   // message only; ToString leaks types and paths
        }
        finally
        {
            job.CompletedAt = _clock.UtcNow;
            _store.ReleaseRunning(job.Id);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Complete();                        // close the front door FIRST
        await base.StopAsync(cancellationToken);  // then signal the token and await the loop
    }
}
```

## How it shuts down cleanly

Four things happen, and the **order is the design**.

### 1. The front door closes before anything else

`StopAsync` calls `_queue.Complete()` *before* `base.StopAsync`. Reverse those two lines and a
request landing mid-shutdown receives `202 Accepted` for work nothing will ever run. With the
queue closed first, `EnqueueAsync` returns `false` and the endpoint answers `503` instead —
which is the truth.

### 2. A job already running gets a grace period, not an execution

```csharp
using var shutdownRegistration = stoppingToken.Register(() =>
    jobCts.CancelAfter(_options.ShutdownGrace));
```

`stoppingToken` firing does **not** cancel the job. It starts a 10-second countdown. Killing a
half-written job the instant shutdown begins isn't graceful, it's just fast. A job that
finishes inside the window completes normally, and its caller gets a real result.

### 3. Everything still queued is drained without running

Running the whole backlog during shutdown would hold the process open for as long as the
backlog takes. Queued jobs are drained and marked `Cancelled` with a reason, so a caller
polling one is told the truth rather than polling a job id forever.

### 4. The loop ends by the queue closing, not by the token

`DequeueAllAsync(CancellationToken.None)` — not `stoppingToken`. This is the subtle one.
Passing the token makes `ReadAllAsync` throw the moment shutdown starts, abandoning the
in-flight job mid-await. Instead the loop ends because the channel was completed in step 1:
`ReadAllAsync` yields what remains, then finishes on its own, and `ExecuteAsync` returns
normally.

### The bound on all of it

```csharp
services.Configure<HostOptions>(o => o.ShutdownTimeout = grace + TimeSpan.FromSeconds(10));
```

The default `ShutdownTimeout` is **5 seconds — shorter than the default 10-second grace
period**. Left alone, the host stops waiting and kills the process while the job is still
inside its grace window, and the grace period achieves nothing but delaying the kill. Deriving
one from the other means they cannot drift apart.

And the contract every handler must honour: **observe your token**. A handler that ignores it
cannot be cancelled and holds shutdown open until the timeout expires and the process is
killed mid-write — the exact outcome all of the above exists to avoid.

### Proof

`tests/QuotesApi.Jobs.Tests` — **8 passed, 0 failed**:

| Test | Asserts |
|---|---|
| `Shutdown_lets_an_in_flight_job_finish_inside_its_grace_period` | job started before shutdown still reaches `Succeeded` |
| `A_job_that_outlasts_the_grace_period_is_cancelled_and_blamed_on_shutdown` | `Cancelled`, attributed to shutdown, and `StopAsync` returns rather than hanging |
| `Jobs_still_queued_at_shutdown_are_drained_without_running` | 3 queued jobs → `Cancelled`, handler ran exactly once |
| `The_queue_refuses_new_work_once_shutdown_has_started` | `EnqueueAsync` returns `false` → endpoint 503 |
| `A_throwing_handler_fails_only_its_own_job_and_the_worker_keeps_going` | loop survives; next job succeeds |
| `A_full_queue_applies_backpressure_rather_than_dropping_jobs` | bounded channel waits, never drops |

Graceful shutdown is tested against `JobProcessor` directly rather than by sending a real
SIGTERM: Git Bash's `kill` on Windows maps to `TerminateProcess`, so the process would die
without ever running `StopAsync` and the test would prove nothing.

---

## The three options, contrasted

| | `IHostedService` | `BackgroundService` | Hangfire |
|---|---|---|---|
| **Shape** | Two events: `StartAsync` / `StopAsync` | A loop between those two events | A job server with a storage-backed queue |
| **Host waits for it?** | Yes — `StartAsync` blocks startup | No — `ExecuteAsync` runs alongside | Runs as a hosted service itself |
| **Survives restart** | ✗ | ✗ | **✓ — jobs live in storage** |
| **Scheduling / cron** | hand-rolled | hand-rolled (`PeriodicTimer`) | **✓ built in** |
| **Retries** | hand-rolled | hand-rolled | **✓ with backoff, automatic** |
| **Multi-instance safe** | ✗ every replica does it | ✗ every replica does it | **✓ one worker takes each job** |
| **Visibility** | your logs | your logs | **✓ dashboard, history** |
| **Infrastructure** | none | none | **a database** |
| **Good for** | fail-fast checks, warmup, one-shot cleanup | draining an in-process queue, polling | scheduled, durable, retried work |

`BackgroundService` **is** an `IHostedService` — one whose `StartAsync` kicks off `ExecuteAsync`
and whose `StopAsync` signals a token and waits. That's the entire class. Both are in this
repository so the difference is visible rather than described:

- `backend/Jobs/JobProcessor.cs` — `BackgroundService`, the long-running drain loop
- `backend/Hosted/JobPipelineDiagnostics.cs` — `IHostedService`, verifying at startup that
  handlers are registered (and **failing the boot** if none are) and reporting queue depth at
  shutdown

The classic mistake is a `while (true)` loop inside `IHostedService.StartAsync`. It deadlocks
startup, because the host is waiting for that method to return.

### Where this design stops being adequate

Everything here is **in-process**, and two assumptions come with that:

1. **Nothing survives a restart.** The queue is a `Channel<T>` in memory and status is a
   `ConcurrentDictionary`. A deploy loses queued work, and a caller polling a job id after one
   gets `404` for work that really did run.
2. **It is per-replica.** Scale to two instances and each has its own queue and its own
   history — a caller may poll the instance that never saw the job.

Both are fine for "generate a report, tell me when it's done". Neither is fine for "email the
customer their invoice". That second case is the Hangfire case, and it is the one-line answer
at the top of this file.

---

## The bug this caught

`JobPipelineDiagnostics` injected `IEnumerable<IJobHandler>` directly. Handlers are **scoped**
(they depend on `IQuoteRepository` → `AppDbContext`) and hosted services are **singletons**, so
the container refused to start:

```
Cannot consume scoped service 'QuotesApi.Jobs.IJobHandler'
from singleton 'Microsoft.Extensions.Hosting.IHostedService'.
```

A captive dependency — and the irony is that it was in the class whose comments warn about
them. Left unchecked it would have produced one handler instance holding one `DbContext`
shared by every job for the life of the process, which fails in ways that look like data
corruption long before they look like a DI mistake.

`ValidateScopes` is on by default in Development, so it failed loudly at boot instead. The fix
is the same one `JobProcessor` already used: take `IServiceScopeFactory` and open a scope.

## Running it

```bash
cd Day18/tests/QuotesApi.Jobs.Tests && dotnet test    # 8 passed
cd Day18 && bash scripts/smoke-jobs.sh                # 13 passed, end-to-end over HTTP
```

The smoke run's headline line:

```
--- POST /api/jobs returns 202 immediately for a 6-second job
  HTTP 202 in 205ms, job 63a3421c-7976-4619-9374-3fbaabda586d
  [PASS] request returned in 205ms — the 6s of work is off the request thread
```

That is the whole exercise in one measurement: a 6-second job, a 205 ms response.
