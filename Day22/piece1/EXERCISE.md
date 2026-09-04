# Day 22 — Resilience with Polly

> **Exercise:** Paste the resilience pipeline. Show logs/metrics of the breaker opening then
> half-opening to recovery.

---

## The resilience pipeline

`backend/Resilience/UpstreamResilience.cs` → `UpstreamResilience.Configure`

```csharp
builder
    // [1] BULKHEAD — cap concurrent calls to this one dependency.
    .AddConcurrencyLimiter(new ConcurrencyLimiterOptions
    {
        PermitLimit = options.MaxConcurrency,       // 4
        QueueLimit = options.MaxQueue,              // 2
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
    })

    // [2] TOTAL TIMEOUT — the promise made to the caller: every attempt, every backoff,
    //     and the wait for a bulkhead slot, all inside this budget.
    .AddTimeout(new TimeoutStrategyOptions
    {
        Timeout = options.TotalTimeout,             // 10s
        OnTimeout = args =>
        {
            log.CountTimeout();
            log.Record("timeout", "total-elapsed", $"after {args.Timeout.TotalSeconds:0.##}s");
            return default;
        }
    })

    // [3] RETRY — exponential backoff with jitter, IDEMPOTENT REQUESTS ONLY.
    .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
    {
        ShouldHandle = args => ValueTask.FromResult(
            args.Context.Properties.GetValue(IsIdempotent, false)
            && IsTransient(args.Outcome)),
        MaxRetryAttempts = options.MaxRetryAttempts, // 3
        Delay = options.RetryBaseDelay,              // 200ms
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        OnRetry = args =>
        {
            // An undisposed response holds its connection. Under a retry storm that is how a
            // resilience pipeline exhausts the socket pool it was added to protect.
            args.Outcome.Result?.Dispose();

            log.CountRetry();
            log.Record("retry", "attempt",
                $"#{args.AttemptNumber + 1} after {args.RetryDelay.TotalMilliseconds:0}ms " +
                $"({Describe(args.Outcome)})");
            return default;
        }
    })

    // [4] CIRCUIT BREAKER — stop calling a dependency that is already down.
    .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
    {
        ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome)),
        FailureRatio = options.FailureRatio,           // 0.5
        SamplingDuration = options.SamplingDuration,   // 10s
        MinimumThroughput = options.MinimumThroughput, // 4
        BreakDuration = options.BreakDuration,         // 5s

        StateProvider = stateProvider,   // exposes live state to GET /api/resilience/state
        ManualControl = manualControl,   // isolate / close, for incidents

        OnOpened = args =>
        {
            log.Record("circuit-breaker", "opened",
                $"breakDuration={args.BreakDuration.TotalSeconds:0.##}s " +
                $"trigger={Describe(args.Outcome)}");
            return default;
        },
        OnHalfOpened = _ =>
        {
            log.Record("circuit-breaker", "half-opened", "break elapsed; admitting one trial call");
            return default;
        },
        OnClosed = args =>
        {
            log.Record("circuit-breaker", "closed",
                args.IsManual ? "manual reset" : "trial call succeeded; traffic restored");
            return default;
        }
    })

    // [5] ATTEMPT TIMEOUT — innermost, bounds one network call.
    .AddTimeout(new TimeoutStrategyOptions
    {
        Timeout = options.AttemptTimeout,            // 1s
        OnTimeout = args =>
        {
            log.CountTimeout();
            log.Record("timeout", "attempt-elapsed", $"after {args.Timeout.TotalSeconds:0.##}s");
            return default;
        }
    });
```

### How it composes

Polly v8 nests strategies like middleware: **the first one added is the outermost**. A call
travels outside-in on the way to the dependency and inside-out on the way back.

```
caller
  -> [1] concurrency limiter   (bulkhead)      outermost
       -> [2] total timeout    (whole operation, retries included)
            -> [3] retry       (idempotent requests only)
                 -> [4] circuit breaker
                      -> [5] attempt timeout   innermost
                           -> HttpClient -> upstream
```

**The bulkhead is outermost** because its job is to cap how much of this process the dependency
may consume, and that only works if it is the first thing a call meets. Further in, callers would
already be holding threads, sockets and retry timers before anything told them to stop — the
exact resource exhaustion it exists to prevent.

**Retry sits outside the breaker** so that every individual attempt is counted as a breaker
sample. Inside, a burst of retries against a dead dependency would register as one sample and the
breaker would never trip. With this order, once the breaker opens, retry receives a
`BrokenCircuitException`, declines to handle it, and the call fails fast — retry handles the
blip, the breaker handles the outage.

**The attempt timeout is inside the breaker** because a timeout is a failure the breaker must
count. A dependency that has stopped answering is precisely the case a breaker exists for.

**Two timeouts, not one.** The inner bounds a single attempt so a hung socket cannot pin a slot.
The outer bounds the caller's total wait — four attempts at 1s each plus backoff is a 5-second
experience for someone promised 1 second, which makes a "resilient" client slower than no
pipeline at all under failure.

---

## Retry only for idempotent operations

```csharp
ShouldHandle = args => ValueTask.FromResult(
    args.Context.Properties.GetValue(IsIdempotent, false) && IsTransient(args.Outcome)),
```

The flag is set by the caller, before the request is sent:

```csharp
var context = ResilienceContextPool.Shared.Get(cancellationToken);
context.Properties.Set(UpstreamResilience.IsIdempotent, IsIdempotent(method));
```

**Why not read the method off the response?** Because the outcomes that most want retrying —
a connection reset, an attempt timeout — have no response to read it from. They arrive as
exceptions with a null `Result`, so a response-sniffing predicate would silently decide "not
idempotent" and stop retrying exactly when retrying matters most.

**Why it matters at all:** a retry cannot distinguish *"the request never arrived"* from *"the
request was processed and the response was lost"*. Both look like a timeout. Retrying a `GET` in
the second case is free. Retrying a `POST` charges the customer twice.

Proved live — same dependency, same 500, same pipeline:

```
--- GET (idempotent) against a failing dependency
  retries: 3
  [PASS] GET retried 3 times (1 attempt + 3 retries)

--- POST (not idempotent) against the same failing dependency
  retries: 0
  [PASS] POST was not retried - one attempt, no duplicate side effects
```

The non-idempotent call still gets the timeout, the breaker and the bulkhead. It just does not
get this.

---

## The breaker opening, then half-opening, then recovering

All output below is from `scripts/verify-resilience.sh`, captured verbatim in
`docs/resilience-verification.txt`. Sixteen assertions, zero failures, three consecutive runs.

### Closed → Open

Twelve calls at a dependency answering 500 to everything. Watch call 8.

```
--- Driving 12 non-idempotent calls at the broken dependency
  call 1  -> HTTP 500  outcome=UpstreamFailed   elapsed=1.8ms   circuit=Closed
  call 2  -> HTTP 500  outcome=UpstreamFailed   elapsed=2.6ms   circuit=Closed
  call 3  -> HTTP 500  outcome=UpstreamFailed   elapsed=1.9ms   circuit=Closed
  call 4  -> HTTP 500  outcome=UpstreamFailed   elapsed=1.7ms   circuit=Closed
  call 5  -> HTTP 500  outcome=UpstreamFailed   elapsed=1.6ms   circuit=Closed
  call 6  -> HTTP 500  outcome=UpstreamFailed   elapsed=1.8ms   circuit=Closed
  call 7  -> HTTP 500  outcome=UpstreamFailed   elapsed=1.3ms   circuit=Closed
  call 8  -> HTTP 500  outcome=UpstreamFailed   elapsed=12.1ms  circuit=Open
  call 9  -> HTTP 503  outcome=CircuitOpen      elapsed=2.9ms   circuit=Open
  call 10 -> HTTP 503  outcome=CircuitOpen      elapsed=0.5ms   circuit=Open
  call 11 -> HTTP 503  outcome=CircuitOpen      elapsed=0.4ms   circuit=Open
  call 12 -> HTTP 503  outcome=CircuitOpen      elapsed=0.4ms   circuit=Open
  [PASS] circuit opened under sustained failure
```

Calls 1–8 each cost a socket, a round trip and a 500. Calls 9–12 cost **0.4ms and nothing else**.

### An open circuit costs nothing

Ten more calls into an open breaker, with the upstream failure counter read before and after:

```
  breakerRejections: 14
  upstreamFailures:  8 -> 8
  sample outcome:    CircuitOpen, 0.4ms
  [PASS] not one of those calls reached the dependency
  [PASS] 14 calls rejected by the breaker, in microseconds each
```

The counter did not move. `breakerRejections` is tracked separately from `upstreamFailures` for
exactly this reason: **they are the cost the breaker removed, not failures it caused.**

### Open → Half-open → Closed

The dependency is repaired while the breaker is still open. The first call after the repair is
**still rejected** — a breaker recovers on a timer, not on news:

```
--- Dependency is repaired, but the breaker does not know yet
  immediate call: outcome=CircuitOpen  circuit=Open
  [PASS] still rejected - recovery is time-based, not health-based

--- Waiting out the 5s break duration

--- One trial call
  HTTP 200  outcome=Succeeded  circuit=Closed
  [PASS] trial call succeeded and the circuit closed - no operator involved
```

The transition log — this is the sequence the exercise asks for:

```
  06:00:09.996  OPENED       breakDuration=5s trigger=HTTP 500
  06:00:10.180  REJECTED     call short-circuited while open
  06:00:10.360  REJECTED     call short-circuited while open
  06:00:10.611  REJECTED     call short-circuited while open
  06:00:10.784  REJECTED     call short-circuited while open
  06:00:11.473  REJECTED     call short-circuited while open
  06:00:18.460  HALF-OPENED  break elapsed; admitting one trial call
  06:00:18.466  CLOSED       trial call succeeded; traffic restored

  last three transitions: opened -> half-opened -> closed
  [PASS] open -> half-open -> closed: the dependency recovered and traffic was restored
```

Half-open lasted **six milliseconds** — exactly one trial call. That is why it has to be recorded
when it happens: nothing could ever poll for it.

### Half-open → Open, when the dependency is still broken

Same wait, dependency still returning 500:

```
  06:00:22.199  OPENED       breakDuration=5s trigger=HTTP 500
  06:00:22.367  REJECTED     call short-circuited while open
  06:00:22.548  REJECTED     call short-circuited while open
  06:00:22.731  REJECTED     call short-circuited while open
  06:00:22.905  REJECTED     call short-circuited while open
  06:00:29.511  HALF-OPENED  break elapsed; admitting one trial call
  06:00:29.513  OPENED       breakDuration=5s trigger=HTTP 500

  [PASS] probe failed, circuit re-opened for another full break duration
```

Recovery is **not assumed on a schedule**. One failed probe and the break starts again, so a
dependency that is still down is not re-flooded the instant its timer expires.

---

## Timeout and bulkhead

```
--- POST (no retries) against a dependency that has stopped answering
  HTTP 504  outcome=TimedOut  elapsed=1068.6ms
  [PASS] call was cut off rather than waiting on a dead dependency
  [PASS] bounded at the 1s attempt timeout, not the 3s the dependency wanted (1068.6ms)

--- 12 concurrent calls against a bulkhead of 2 permits + 1 queue slot
  admitted: 3    shed: 9
  bulkheadRejections counter: 9
  sample rejection: bulkhead is full; shed to protect the rest of the process (13.8ms)
  [PASS] 9 callers were shed fast instead of all 12 queueing behind a slow dependency
  [PASS] at most permits+queue (3) were admitted at once
  [PASS] bulkhead rejections never reached the breaker - shedding is not a dependency failure
```

A dependency that stops answering is more dangerous than one that fails, because failure is fast
and slowness is contagious. The timeout stops one call from waiting; the bulkhead stops all
twelve from queueing behind it.

That last assertion matters: bulkhead rejections are thrown by the **outermost** strategy, so
they never reach the breaker. Load shed for lack of capacity is not evidence that the dependency
is unhealthy, and counting it as such would let a traffic spike open a breaker on a service that
was working perfectly.

---

## How the breaker is observable

Polly's `CircuitBreakerStateProvider` is registered as a singleton and handed to the breaker, so
live state is readable from outside:

```
GET /api/resilience/state
{
  "circuitState": "Closed",
  "closed": true,
  "configuration": {
    "failureRatio": 0.5,
    "minimumThroughput": 8,
    "samplingDurationSeconds": 30,
    "breakDurationSeconds": 5,
    "maxRetryAttempts": 3,
    "attemptTimeoutSeconds": 1,
    "totalTimeoutSeconds": 10,
    "bulkheadConcurrency": 2,
    "bulkheadQueue": 1
  }
}
```

The state provider answers *what state the next call will meet*. The event log answers *what
happened, in what order*. Both are needed — a counter can tell you the breaker opened five times,
but not whether it ever closed again.

```
GET /api/resilience/events?transitionsOnly=true    ordered breaker transitions
GET /api/resilience/stats                          calls, failures, retries, timeouts,
                                                   breakerRejections, bulkheadRejections
```

---

## What would break this

**A second `HttpClient` for the same dependency.** A pipeline only protects what goes through it.
One stray `new HttpClient()` elsewhere in the codebase shares the dependency's failures without
sharing its breaker or its bulkhead, and the breaker's failure ratio is computed from a sample
that no longer represents the traffic.

**`MinimumThroughput` set below `MaxRetryAttempts + 1`.** One caller's own retry sequence would
then be enough to open the breaker, taking the dependency away from every other caller in the
process. There is a test for this (`Breaker_opening_mid_retry_cuts_the_remaining_attempts_short`)
because it is easy to tune into existence and invisible until load arrives.

**Counting 4xx as transient.** One client sending malformed requests would open the breaker for
everybody.

**Leaving `HttpClient.Timeout` at its default.** Two clocks bounding one condition; whichever
fires first decides the exception type, and a pipeline tuned around `TimeoutRejectedException`
starts seeing `TaskCanceledException` the moment the numbers cross.

**Per-request pipeline construction.** The breaker and the bulkhead are *state*. Built per
request, they reset constantly and never trip — the code reviews as correct and does nothing.

**An unbounded bulkhead queue.** It converts a downstream slowdown into unbounded memory growth,
and turns fast rejection into slow timeout: the same outage with a longer fuse.

**Retrying without idempotency.** Not a resilience bug, a correctness one. It does not show up in
latency graphs; it shows up in duplicate charges.

---

## Reproducing

```bash
cd Day22/piece1

# unit proof — 40 tests over the real production pipeline
dotnet test tests/QuotesApi.Resilience.Tests --nologo

# live proof — real process, real sockets, 16 assertions
bash scripts/verify-resilience.sh
```

Screenshots of every section are in [`Screenshots/`](Screenshots), and the raw captured output is
in [`docs/resilience-verification.txt`](docs/resilience-verification.txt).
