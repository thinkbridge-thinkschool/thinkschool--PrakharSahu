# Day 22 — what changed, where, and why

Day 22 is a delta on Day 21. This file lists **only the code I added or changed**, the exact file
and method it lives in, what the thing being used actually *is*, and why it was done that way.

Nothing from Day 17–21 was removed except one Week-1 stub, which is called out in section 8.
Day 21 and earlier folders are untouched.

---

## Definitions first

**Polly** is the .NET resilience library. Version 8 replaced the old "policy" objects with a
**pipeline**: strategies are added to a builder and composed like nested middleware. The first
strategy added is the **outermost** — a call travels outside-in on the way to the dependency and
inside-out on the way back. Order is therefore not a style choice; it is the design.

**Retry with exponential backoff and jitter** — repeat a failed call, waiting longer after each
failure, with a random offset. Backoff exists because retrying instantly aims a burst at a
dependency that has just said it is struggling. Jitter exists because without it, every client
that failed at the same moment retries at the same moment, and that synchronised herd is often
what keeps the dependency down.

**Idempotent** — repeating the request leaves the server in the same state as sending it once.
Not that the response is identical: a `GET` whose body changes between calls is still idempotent,
because reading it again changed nothing. Per RFC 9110, `GET`, `HEAD`, `PUT`, `DELETE`, `OPTIONS`
and `TRACE` are idempotent; `POST` and `PATCH` are not. This matters because **a retry cannot
tell "the request never arrived" from "the request was processed and the response was lost"** —
both look like a timeout. Retrying the read is free. Retrying the write charges the customer
twice.

**Circuit breaker** — a three-state machine in front of a dependency.

| State | Behaviour |
|---|---|
| **Closed** | Calls flow through. Failures are counted over a rolling window. |
| **Open** | Every call is rejected instantly, without touching the network, for `BreakDuration`. |
| **Half-open** | Exactly **one** trial call is admitted. Success closes the breaker; failure re-opens it. |

Half-open is the whole reason a breaker recovers on its own instead of needing an operator, and
it is why recovery is cheap: one probe, not a stampede.

**Bulkhead** — named after ship compartments: flooding one must not sink the vessel. It caps how
many calls may be in flight to one dependency at a time. In Polly v8 it is a **concurrency
limiter** (`AddConcurrencyLimiter`). Without one, a dependency that slows from 20ms to 5s does
not just make its own calls slow — it parks every request thread that touches it, and endpoints
with no relationship to that dependency start timing out too. The limit converts "everything
degrades" into "this one feature degrades".

**Attempt timeout vs total timeout** — the first bounds one network call, the second bounds the
whole operation including every retry and every backoff. Both are needed: with only an attempt
timeout, four attempts at 1s each plus backoff is a 5-second experience for a caller who was
promised 1 second, which makes the "resilient" client *slower* than no pipeline at all under
failure.

---

## 1. Package added

**File:** `backend/QuotesApi.csproj`

```xml
<PackageReference Include="Polly.RateLimiting" Version="8.4.2" />
```

**Why:** the bulkhead (`AddConcurrencyLimiter`) lives in `Polly.RateLimiting`. It already arrived
transitively through `Microsoft.Extensions.Http.Resilience` 10.9.0, which was already referenced,
but this code calls it **directly** — and a direct call on a transitive package breaks the day
that package changes its own dependency graph. Pinned to the version the resilience package
already resolves, so no version conflict is introduced.

---

## 2. New file — the pipeline. This is the core of the day.

**File:** `backend/Resilience/UpstreamResilience.cs`
**Method:** `UpstreamResilience.Configure`

```csharp
public static void Configure(
    ResiliencePipelineBuilder<HttpResponseMessage> builder,
    UpstreamOptions options,
    ResilienceEventLog log,
    CircuitBreakerStateProvider stateProvider,
    CircuitBreakerManualControl manualControl)
{
    builder
        // [1] BULKHEAD — outermost
        .AddConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = options.MaxConcurrency,
            QueueLimit = options.MaxQueue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        })

        // [2] TOTAL TIMEOUT — the promise made to the caller
        .AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = options.TotalTimeout,
            OnTimeout = args => { log.CountTimeout(); /* ... */ return default; }
        })

        // [3] RETRY — idempotent requests only
        .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            ShouldHandle = args => ValueTask.FromResult(
                args.Context.Properties.GetValue(IsIdempotent, false)
                && IsTransient(args.Outcome)),
            MaxRetryAttempts = options.MaxRetryAttempts,
            Delay = options.RetryBaseDelay,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            OnRetry = args =>
            {
                args.Outcome.Result?.Dispose();
                log.CountRetry();
                log.Record("retry", "attempt", /* ... */);
                return default;
            }
        })

        // [4] CIRCUIT BREAKER
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome)),
            FailureRatio = options.FailureRatio,
            SamplingDuration = options.SamplingDuration,
            MinimumThroughput = options.MinimumThroughput,
            BreakDuration = options.BreakDuration,
            StateProvider = stateProvider,
            ManualControl = manualControl,
            OnOpened     = args => { log.Record("circuit-breaker", "opened", /* ... */); return default; },
            OnHalfOpened = _    => { log.Record("circuit-breaker", "half-opened", /* ... */); return default; },
            OnClosed     = args => { log.Record("circuit-breaker", "closed", /* ... */); return default; }
        })

        // [5] ATTEMPT TIMEOUT — innermost
        .AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = options.AttemptTimeout,
            OnTimeout = args => { log.CountTimeout(); /* ... */ return default; }
        });
}
```

### The composition

```
caller
  -> [1] concurrency limiter   (bulkhead)      outermost
       -> [2] total timeout    (whole operation, retries included)
            -> [3] retry       (idempotent requests only)
                 -> [4] circuit breaker
                      -> [5] attempt timeout   innermost
                           -> HttpClient -> upstream
```

### Why this order

**The bulkhead is outermost** because its job is to cap how much of this process the dependency
is allowed to consume. That only works if it is the first thing a call meets. Placed further in,
callers would already be holding threads, sockets and retry timers before anything told them to
stop — which is exactly the resource exhaustion it exists to prevent.

**Retry sits outside the breaker** because the breaker has to count every individual attempt,
retried ones included. If retry were inside, a burst of retries against a dead dependency would
register as *one* sample and the breaker would never trip. With this order each retry passes
through the breaker and is counted; once the breaker opens it rejects immediately, the retry
strategy sees a `BrokenCircuitException`, declines to handle it (it is not transient), and the
whole call fails fast. **Retry handles the blip; the breaker handles the outage.**

**The attempt timeout is inside the breaker** because a timeout is a failure the breaker must
count. A dependency that has stopped answering is precisely the case a breaker exists for, and it
is invisible if timeouts expire outside the counting.

### Why the retry predicate reads from the context, not the response

```csharp
ShouldHandle = args => ValueTask.FromResult(
    args.Context.Properties.GetValue(IsIdempotent, false) && IsTransient(args.Outcome)),
```

The obvious implementation reads the method off `args.Outcome.Result.RequestMessage`. It is
wrong, because **the outcomes that most want retrying have no response at all** — a connection
reset, an attempt timeout. Those arrive as exceptions with a null `Result`, and the predicate
would silently fall through to "not idempotent" and stop retrying exactly when retrying matters
most.

The caller knows the method before it sends. That is the only moment the answer is reliably
available, so it is stamped on the `ResilienceContext` there:

```csharp
public static readonly ResiliencePropertyKey<bool> IsIdempotent = new("upstream.idempotent");
```

This is also **why this day uses `AddResiliencePipeline` rather than `HttpClient`'s
`AddResilienceHandler`.** With the HTTP handler, the handler creates the context, not the caller,
so there is no clean place to put that flag.

### The shared transient predicate

**Method:** `UpstreamResilience.IsTransient`

```csharp
public static bool IsTransient(Outcome<HttpResponseMessage> outcome)
{
    if (outcome.Exception is not null)
    {
        return outcome.Exception is HttpRequestException
            or TimeoutRejectedException
            or TaskCanceledException;
    }

    var status = outcome.Result?.StatusCode;

    return status is >= HttpStatusCode.InternalServerError
        or HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests;
}
```

**Why one predicate for both retry and the breaker:** they are asking the same question. If they
disagreed, the pipeline could retry failures the breaker ignores (and never trip), or trip on
failures it refuses to retry.

**Why 4xx is excluded apart from 408 and 429:** a 400 or a 404 means the request was wrong, and
it will be exactly as wrong the second time. Retrying wastes a call, and — worse — *counting* it
would let one client's malformed requests open the breaker for every other caller in the process.
429 is included because the server is explicitly asking for a pause, which is what backoff
provides.

**Why `InvalidOperationException` is not transient:** a bug on our side is not the dependency's
fault. Counting it would let a `NullReferenceException` in our own code open a breaker on a
perfectly healthy service.

### One easy-to-miss detail

```csharp
OnRetry = args =>
{
    args.Outcome.Result?.Dispose();
```

The response being retried away is never read again, and an undisposed `HttpResponseMessage`
holds its connection. Under a retry storm that is how a resilience pipeline exhausts the very
socket pool it was added to protect. Polly's own HTTP integration does this for you; a
hand-composed pipeline does not.

---

## 3. New file — the typed client

**File:** `backend/Resilience/UpstreamClient.cs`
**Methods:** `ReadAsync`, `NotifyAsync`, `CallAsync`, `IsIdempotent`

```csharp
private async Task<UpstreamCallResult> CallAsync(
    HttpMethod method, string path, CancellationToken cancellationToken)
{
    var idempotent = IsIdempotent(method);
    var stopwatch = Stopwatch.StartNew();

    var context = ResilienceContextPool.Shared.Get(cancellationToken);
    context.Properties.Set(UpstreamResilience.IsIdempotent, idempotent);

    try
    {
        _log.CountUpstreamCall();

        var response = await _pipeline.ExecuteAsync(
            static async (ctx, state) =>
            {
                using var request = new HttpRequestMessage(state.Method, state.Path);
                return await state.Http.SendAsync(request, ctx.CancellationToken);
            },
            context,
            (Http: _http, Method: method, Path: path));
        // ... classify the response ...
    }
    catch (BrokenCircuitException)        { /* -> UpstreamOutcome.CircuitOpen */ }
    catch (RateLimiterRejectedException)  { /* -> UpstreamOutcome.BulkheadRejected */ }
    catch (TimeoutRejectedException)      { /* -> UpstreamOutcome.TimedOut */ }
    catch (HttpRequestException ex)       { /* -> UpstreamOutcome.UpstreamFailed */ }
    finally { ResilienceContextPool.Shared.Return(context); }
}

public static bool IsIdempotent(HttpMethod method) =>
    method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Put
    || method == HttpMethod.Delete || method == HttpMethod.Options || method == HttpMethod.Trace;
```

**Why a single choke point:** a pipeline only protects what goes through it. A second code path
that quietly news up an `HttpClient` shares the dependency's failures without sharing its breaker,
its bulkhead or its budget. Centralising the call is what makes the guarantees hold process-wide
rather than per-call-site.

**Why the request is created inside the callback:** an `HttpRequestMessage` cannot be sent twice.
Creating it outside and reusing it across retries throws on the second attempt, and the retry
strategy silently stops working.

**Why the context is pooled:** it is allocated on every outbound call, and on a hot path that
allocation is pure waste. It must be returned in a `finally`.

**What this class deliberately does not do:** it never decides whether to retry. It states a fact
only the caller can state — whether the operation is safe to repeat — and the pipeline decides
what to do with that.

---

## 4. New file — observability of the breaker's state machine

**File:** `backend/Resilience/ResilienceEventLog.cs`

```csharp
public sealed record ResilienceEvent(DateTimeOffset At, string Strategy, string Event, string Detail);

public void Record(string strategy, string @event, string detail = "")
{
    _events.Enqueue(new ResilienceEvent(DateTimeOffset.UtcNow, strategy, @event, detail));
    while (_events.Count > MaxRetained && _events.TryDequeue(out _)) { }
}

public IReadOnlyList<ResilienceEvent> StateTransitions() =>
    _events.Where(e => e.Strategy == "circuit-breaker").ToArray();
```

**Why a log and not just counters:** the interesting thing about a circuit breaker is the
**sequence**. "Opened, then half-opened, then closed" is a different story from "opened,
half-opened, opened again", and a counter cannot tell them apart. Half-open in particular exists
for a single trial call — it is gone before anything can poll for it, so it has to be recorded
at the moment it happens.

**Why counters as well:** the log answers "what happened, in what order"; the counters answer
"how much". A load test needs the second without re-reading the first.

**Why it is bounded at 300:** an unbounded list fed by every retry of every request is a memory
leak with a respectable name.

`breakerRejections` is tracked separately from `upstreamFailures` on purpose. A thousand
rejections and a thousand failed calls cost wildly different amounts and mean different things:
rejections never reached the network at all. They are **the cost the breaker removed, not
failures it caused**.

---

## 5. New file — the settings

**File:** `backend/Resilience/UpstreamOptions.cs`

Every value is bindable from configuration, which is how `scripts/verify-resilience.sh` tunes the
demo without editing the shipped defaults.

| Setting | Default | Note |
|---|---|---|
| `AttemptTimeout` | 1s | bounds one network call |
| `TotalTimeout` | 10s | bounds the whole operation |
| `MaxRetryAttempts` | 3 | so four attempts in total |
| `RetryBaseDelay` | 200ms | exponential, jittered |
| `FailureRatio` | 0.5 | fraction of failures that trips the breaker |
| `SamplingDuration` | 10s | the rolling window the ratio is measured over |
| `MinimumThroughput` | 4 | calls required before the ratio means anything |
| `BreakDuration` | 5s | how long the breaker stays open |
| `MaxConcurrency` | 4 | bulkhead permits |
| `MaxQueue` | 2 | bulkhead waiting room |

**Why `MinimumThroughput` exists at all:** without a floor, one failed call out of one is a 100%
failure ratio, and a quiet service trips its own breaker on a single blip.

**Why `TotalTimeout` must exceed `AttemptTimeout × (MaxRetryAttempts + 1)` plus the backoff:**
otherwise the total timeout fires mid-retry and the retries were pointless. Getting this
relationship wrong is the most common way a "resilient" client ends up slower and no more
reliable than a plain one.

**Why `MaxQueue` is small but not zero:** zero would reject the instant all permits are busy,
which is too brittle for normal jitter. Unbounded is worse — it converts a downstream slowdown
into unbounded memory growth and turns rejection into timeout, which is the same outage with a
longer fuse.

**`BaseAddress` resolves itself.** It is empty by default and read back from the addresses
Kestrel actually bound (`IServerAddressesFeature`), because every verification script in this repo
picks its own free port. A hard-coded port would make the outbound call land on whatever else
happens to be listening there — or on nothing at all.

---

## 6. New file — the controllable dependency

**File:** `backend/Resilience/UpstreamFaults.cs`

```csharp
public enum UpstreamFaultMode { None, ServerError, Slow, BadRequest }

public sealed class UpstreamFaults
{
    private volatile UpstreamFaultMode _mode = UpstreamFaultMode.None;
    public UpstreamFaultMode Mode { get => _mode; set => _mode = value; }
    public int LatencyMs { get; set; } = 2000;
}
```

**Why a plain mutable singleton and not `IOptions<T>`** (same reason as Day 21's `CacheOptions`):
the proof has to flip the dependency from healthy to broken and back **inside one process**,
because a circuit breaker's state only means something across a continuous timeline. Restarting
the app to change a setting would reset the breaker and destroy the very thing being measured.

---

## 7. New file — the endpoints

**File:** `backend/Endpoints/ResilienceEndpoints.cs`

| Endpoint | Purpose |
|---|---|
| `GET /upstream/quote-of-the-day` | the fake dependency, idempotent |
| `POST /upstream/notify` | the fake dependency, not idempotent |
| `GET /api/resilience/call` | drives the pipeline with a **GET** — retries apply |
| `POST /api/resilience/call-write` | drives the pipeline with a **POST** — retries do not |
| `GET /api/resilience/state` | live circuit state + the configuration in force |
| `GET /api/resilience/events` | ordered event log; `?transitionsOnly=true` for the breaker only |
| `GET /api/resilience/stats` | the counters |
| `POST /api/resilience/reset` | zero the log |
| `POST /api/resilience/breaker/{isolate\|close}` | manual override |
| `POST /api/resilience/upstream/faults` | **Development only** — switch the dependency's behaviour |

**Why the fake upstream is a real HTTP endpoint in the same process** rather than a mocked
`HttpMessageHandler`: the call goes over a real socket, with real status codes, real connection
handling and a real timeout when it stops answering. Mocking the handler would prove the pipeline
is *wired up* and nothing about how it *behaves* — and "behaves" is what the exercise asks to be
shown.

**Why it lives outside `/api`:** the `CallerIdentity` middleware Day 17 added demands an Entra
app-only token on `/api/*`. The process calling itself would be turned away by its own security.

**Why `Results.Json(..., statusCode: result.Ok ? 200 : ...)` and not always 200:** a pipeline that
swallows every failure into a cheerful 200 has not made the system resilient, it has made the
failure **invisible** — which is worse, because now nothing alerts.

**Why the fault endpoint is Development-only** and returns before registration otherwise: its
whole purpose is to make a dependency fail on demand. In any environment where the dependency is
real, that is not a diagnostic, it is an outage with an HTTP API. Not registering it means it
cannot be reached, rather than merely being discouraged.

**Why `ManualControl` is worth having:** `isolate` holds the circuit open regardless of health —
the kill switch for taking a dependency out of rotation mid-incident without a deploy. An
automatic breaker can only react to failures that have already happened, and sometimes you know a
dependency is about to be unavailable before it starts failing.

---

## 8. New file — the wiring

**File:** `backend/Extensions/ResilienceExtensions.cs`
**Method:** `AddUpstreamResilience`

```csharp
services.AddSingleton(options);
services.AddSingleton<ResilienceEventLog>();
services.AddSingleton<UpstreamFaults>();
services.AddSingleton<CircuitBreakerStateProvider>();
services.AddSingleton<CircuitBreakerManualControl>();

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
    client.Timeout = Timeout.InfiniteTimeSpan;
});
```

**Why the pipeline build callback runs once, and why that matters:** that single instance is what
makes the breaker and the bulkhead **process-wide state**. Build one per request and they are
objects that reset constantly and never trip — the pipeline would look correct in code review and
do nothing at runtime.

**Why `client.Timeout = Timeout.InfiniteTimeSpan`:** Polly's attempt timeout should be the only
clock bounding a call. Leaving `HttpClient`'s own 100-second default in place gives one condition
two owners — whichever fires first decides the exception type, so a pipeline tuned around
`TimeoutRejectedException` starts seeing `TaskCanceledException` instead the moment the numbers
cross.

### What was removed

**File:** `backend/Program.cs`

The Week-1 stub this day replaces:

```csharp
// REMOVED
builder.Services.AddHttpClient("ExternalService")
    .AddResilienceHandler("default", b =>
    {
        b.AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 3, ... })
         .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions { ... })
         .AddTimeout(TimeSpan.FromSeconds(10));
    });

app.MapGet("/test-retry", async (IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("ExternalService");
    var response = await client.GetAsync("https://httpstat.us/500");
    return Results.Content($"Status: {response.StatusCode}");
});
```

Three things were wrong with it, and each maps to a piece of this day's work:

1. **It retried every request** regardless of whether repeating it was safe — a `POST` through
   that client would have been sent four times.
2. **It had no concurrency limit at all**, so a slow dependency could consume every request
   thread in the process.
3. **It reported nothing about the breaker**, so there was no way to tell an open circuit from a
   dependency that had merely gone quiet.

It also called `https://httpstat.us`, so the demo depended on a third-party service being up.

---

## 9. Tests — `tests/QuotesApi.Resilience.Tests` (new project, 40 tests)

**Files:** `PipelineHarness.cs`, `ResiliencePipelineTests.cs`

The harness builds the **real production pipeline** — it calls the same
`UpstreamResilience.Configure` that `Program.cs` calls — and lowers only the timings.

```csharp
var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
UpstreamResilience.Configure(builder, Options, Log, StateProvider, ManualControl);
Pipeline = builder.Build();
```

**Why not rebuild the pipeline in the test:** a test that composes its own strategies passes
happily while the shipped one is ordered wrong, which is the single bug it most needs to catch.

Coverage, grouped by the promise being tested:

| Group | What is proved |
|---|---|
| Retry | idempotent calls retried to the limit; non-idempotent never; 4xx not retried; 408/429 retried; retry stops on recovery |
| Circuit breaker | starts closed; opens under sustained failure; open circuit calls the dependency **zero** times; open circuit stops retry hammering; half-opens then closes on success; re-opens on a failed probe; 4xx never opens it; does not open below minimum throughput |
| Timeouts | attempt timeout bounds one call; it is retried; it counts against the breaker; total timeout bounds the whole operation |
| Bulkhead | rejects once permits and queue are exhausted; lets calls through again once slots free |
| Classification | HTTP methods per RFC 9110; status codes transient vs permanent; transport failures transient; our own bugs not |

### A bug the tests found

The first run failed five tests. The default harness used `MinimumThroughput = 2`, and:

> retrying one 500 four times produces **four breaker samples**. The breaker opened on the
> second, and the third attempt was short-circuited — so the retry tests measured the breaker and
> reported 2 attempts where 4 were configured.

That is correct behaviour, wrongly asserted. The fix was to hold the breaker out of the way in
tests that are about retry (`MinimumThroughput = 100`), expose
`PipelineHarness.WithLiveBreaker()` for tests that are about the breaker, and give the
interaction its own test:

```csharp
[Fact]
public async Task Breaker_opening_mid_retry_cuts_the_remaining_attempts_short()
```

It is worth stating as a test rather than discovering in production: **a retry count is a ceiling,
not a promise.** Tune `MinimumThroughput` below `MaxRetryAttempts + 1` and a single caller can
open the breaker on its own retries, taking the dependency away from every other caller in the
process. The shipped defaults (`MinimumThroughput = 4`, four attempts) keep the threshold at one
caller's full retry sequence, which is the tightest it should ever be.

---

## 10. New file — the live proof

**File:** `scripts/verify-resilience.sh`
**Captured output:** `docs/resilience-verification.txt`

Eight sections against a real running process, over real sockets. Result: **16 passed, 0 failed**,
three consecutive runs.

| Section | Proves |
|---|---|
| 1 | healthy baseline, circuit closed |
| 2 | GET retried 3×, POST retried 0×, same fault |
| 3 | circuit **closed → open** under sustained failure |
| 4 | open circuit rejects without touching the network |
| 5 | **open → half-open → closed** |
| 6 | **open → half-open → open** when the probe fails |
| 7 | attempt timeout bounds a slow dependency |
| 8 | bulkhead sheds concurrent load |

The headline evidence, section 3 — watch call 8:

```
call 6  -> HTTP 500  outcome=UpstreamFailed   elapsed=1.8ms   circuit=Closed
call 7  -> HTTP 500  outcome=UpstreamFailed   elapsed=1.3ms   circuit=Closed
call 8  -> HTTP 500  outcome=UpstreamFailed   elapsed=12.1ms  circuit=Open
call 9  -> HTTP 503  outcome=CircuitOpen      elapsed=2.9ms   circuit=Open
call 10 -> HTTP 503  outcome=CircuitOpen      elapsed=0.5ms   circuit=Open
call 11 -> HTTP 503  outcome=CircuitOpen      elapsed=0.4ms   circuit=Open
```

and section 5, the recovery the exercise asks for:

```
06:00:09.996  OPENED       breakDuration=5s trigger=HTTP 500
06:00:10.180  REJECTED     call short-circuited while open
   ...
06:00:18.460  HALF-OPENED  break elapsed; admitting one trial call
06:00:18.466  CLOSED       trial call succeeded; traffic restored
```

### Two bugs the script found in itself

Both were **wrong assertions, not wrong behaviour**, and both had the same shape: a section
assumed a break duration had not yet elapsed, when the section before it had already outlasted
one.

1. Section 5's "the dependency is repaired but the breaker does not know yet" call was arriving
   *after* the 5-second break, so it found a half-open circuit and succeeded — the demo
   accidentally proved the opposite of its own point.
2. Section 4's "not one of these calls reached the dependency" saw `upstreamFailures` move by
   one, because a half-open probe legitimately got through mid-section.

Both now **re-open the circuit from scratch** so the break window is known to have just started.
Timing that depends on how fast the previous section happened to run is not evidence.

Two harness bugs were fixed alongside them: `call_read`/`call_write` had `>/dev/null` *inside*
the function, so every `CODE=$(call_write ...)` captured an empty string; and section 8 used a
bare `wait`, which also waits on the API process started earlier in the same shell — the script
hung forever and looked like a deadlock in the bulkhead.

---

## 11. Screenshots

**File:** `scripts/build-screenshot-cards.mjs` → `Screenshots/*.jpg`

Every line of terminal output on those cards is sliced out of `docs/resilience-verification.txt`
and `docs/test-results.txt` **at build time**. Nothing is retyped, so a card cannot drift away
from the run it claims to show.

---

## Files touched

| File | Change |
|---|---|
| `backend/QuotesApi.csproj` | + `Polly.RateLimiting` |
| `backend/Resilience/UpstreamResilience.cs` | **new** — the pipeline |
| `backend/Resilience/UpstreamClient.cs` | **new** — the typed client |
| `backend/Resilience/UpstreamOptions.cs` | **new** — settings |
| `backend/Resilience/UpstreamFaults.cs` | **new** — fault switch |
| `backend/Resilience/ResilienceEventLog.cs` | **new** — observability |
| `backend/Extensions/ResilienceExtensions.cs` | **new** — DI wiring |
| `backend/Endpoints/ResilienceEndpoints.cs` | **new** — fake upstream, callers, stats |
| `backend/Program.cs` | + `AddUpstreamResilience`, + `MapResilienceEndpoints`, − Week-1 stub |
| `tests/QuotesApi.Resilience.Tests/` | **new** — 40 tests |
| `scripts/verify-resilience.sh` | **new** — live proof |
| `scripts/build-screenshot-cards.mjs` | **new** — screenshot generator |
| `docs/resilience-verification.txt` | **new** — captured output |
| `docs/test-results.txt` | **new** — captured test output |
| `Screenshots/` | **new** — 8 evidence cards |

Frontend: **unchanged**. Day 22 is a backend-only concern; the frontend was rebuilt and retested
only to confirm nothing regressed (147 tests, production build clean).
