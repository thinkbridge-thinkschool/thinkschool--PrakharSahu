using System.Net;
using System.Threading.RateLimiting;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace QuotesApi.Resilience;

/// <summary>
/// The resilience pipeline that wraps every outbound call to the upstream dependency.
/// </summary>
/// <remarks>
/// <para>
/// Four strategies, and the <b>order they are added is the design</b>. Polly v8 composes them
/// like nested middleware: the first strategy added is the outermost, and a call travels
/// outside-in on the way to the dependency and inside-out on the way back.
/// </para>
/// <code>
///   caller
///     -> [1] concurrency limiter   (bulkhead)      outermost
///          -> [2] total timeout    (whole operation, retries included)
///               -> [3] retry       (idempotent requests only)
///                    -> [4] circuit breaker
///                         -> [5] attempt timeout   innermost
///                              -> HttpClient -> upstream
/// </code>
/// <para>
/// <b>Why the bulkhead is outermost.</b> Its job is to cap how much of this process the
/// dependency is allowed to consume. That only works if it is the first thing a call meets —
/// placed further in, callers would already be holding threads, sockets and retry timers before
/// anything told them to stop, which is the resource exhaustion the bulkhead exists to prevent.
/// </para>
/// <para>
/// <b>Why retry sits outside the breaker.</b> The breaker has to count every individual attempt,
/// including retried ones, or a burst of retries against a dead dependency never registers as
/// failure and the breaker never trips. With this order each retry passes through the breaker
/// and is counted; once the breaker opens it rejects immediately, the retry strategy sees a
/// <see cref="BrokenCircuitException"/>, declines to handle it, and the whole call fails fast.
/// Retry and the breaker cooperate rather than fight: retry handles the blip, the breaker
/// handles the outage.
/// </para>
/// <para>
/// <b>Why there are two timeouts.</b> The inner one bounds a single attempt so a hung socket
/// cannot pin a slot forever, and it must be short enough that retrying is still worthwhile.
/// The outer one bounds the caller's total wait, because four attempts each honouring a 1s
/// timeout plus backoff is a 5s experience for a user who was promised 1s. A pipeline with only
/// an attempt timeout is slower than no pipeline at all under failure.
/// </para>
/// </remarks>
public static class UpstreamResilience
{
    public const string PipelineKey = "upstream";

    /// <summary>
    /// Set per call to say whether retrying this particular request is safe.
    /// </summary>
    /// <remarks>
    /// Carried on the <see cref="ResilienceContext"/> rather than sniffed from the response,
    /// because the outcomes that most want retrying — a connection reset, an attempt timeout —
    /// have no response at all to sniff. The caller knows the method before it sends; that is
    /// the only moment the answer is reliably available.
    /// </remarks>
    public static readonly ResiliencePropertyKey<bool> IsIdempotent = new("upstream.idempotent");

    /// <summary>
    /// Builds the pipeline. Called once; the resulting pipeline is shared by every caller,
    /// which is what makes the breaker and the bulkhead process-wide rather than per-request.
    /// </summary>
    public static void Configure(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        UpstreamOptions options,
        ResilienceEventLog log,
        CircuitBreakerStateProvider stateProvider,
        CircuitBreakerManualControl manualControl)
    {
        builder
            // -------------------------------------------------------------------------------
            // [1] BULKHEAD - cap concurrent calls to this one dependency.
            //
            // Named after ship compartments: flooding one must not sink the vessel. Without it,
            // a dependency that slows from 20ms to 5s does not just make its own calls slow. It
            // parks every request thread that touches it, and endpoints with no relationship to
            // this dependency start timing out too. The limit converts "everything degrades"
            // into "this one feature degrades", which is the entire point.
            //
            // The queue is small and finite. Rejection here is a feature: a fast, explicit
            // "no capacity" is strictly better than an unbounded queue that turns every
            // caller's failure into a timeout several seconds later.
            // -------------------------------------------------------------------------------
            .AddConcurrencyLimiter(new ConcurrencyLimiterOptions
            {
                PermitLimit = options.MaxConcurrency,
                QueueLimit = options.MaxQueue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            })

            // -------------------------------------------------------------------------------
            // [2] TOTAL TIMEOUT - the promise made to the caller.
            //
            // Covers the whole operation: every attempt, every backoff delay, and the wait for
            // a bulkhead slot. Whatever the pipeline does internally, the caller waits no
            // longer than this.
            // -------------------------------------------------------------------------------
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = options.TotalTimeout,
                OnTimeout = args =>
                {
                    log.CountTimeout();
                    log.Record("timeout", "total-elapsed", $"after {args.Timeout.TotalSeconds:0.##}s");
                    return default;
                }
            })

            // -------------------------------------------------------------------------------
            // [3] RETRY - exponential backoff with jitter, IDEMPOTENT REQUESTS ONLY.
            //
            // The idempotency guard is the part that is easy to skip and expensive to skip. A
            // retry cannot tell "the request never arrived" from "the request was processed and
            // the response was lost" - both look like a timeout. Retrying a GET in the second
            // case is free. Retrying a POST charges the customer twice. So retry is gated on
            // the caller's own declaration that the operation is safe to repeat, and a
            // non-idempotent call still gets the timeout, the breaker and the bulkhead - just
            // not this.
            //
            // Exponential backoff, because retrying immediately aims a burst at a dependency
            // that just said it was struggling. Jitter, because without it every client that
            // failed at the same moment retries at the same moment, and the synchronised herd
            // is often what keeps the dependency down.
            // -------------------------------------------------------------------------------
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
                    // The response being retried away is never read again, and an undisposed
                    // HttpResponseMessage holds its connection. Under a retry storm that is how
                    // a resilience pipeline exhausts the socket pool it was added to protect.
                    args.Outcome.Result?.Dispose();

                    log.CountRetry();
                    log.Record(
                        "retry",
                        "attempt",
                        $"#{args.AttemptNumber + 1} after {args.RetryDelay.TotalMilliseconds:0}ms " +
                        $"({Describe(args.Outcome)})");
                    return default;
                }
            })

            // -------------------------------------------------------------------------------
            // [4] CIRCUIT BREAKER - stop calling a dependency that is already down.
            //
            // Three states. CLOSED: calls flow, failures are counted over a rolling window.
            // OPEN: every call is rejected instantly without touching the network, for
            // BreakDuration. HALF-OPEN: exactly one trial call is admitted - it closes the
            // breaker if it succeeds and re-opens it if it fails.
            //
            // Half-open is the whole reason a breaker recovers on its own instead of needing an
            // operator. It is also why recovery is cheap: one probe, not a stampede.
            //
            // The failure ratio needs MinimumThroughput underneath it, or one failed call out
            // of one is a 100% failure rate and a quiet service trips itself on a single blip.
            // -------------------------------------------------------------------------------
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome)),
                FailureRatio = options.FailureRatio,
                SamplingDuration = options.SamplingDuration,
                MinimumThroughput = options.MinimumThroughput,
                BreakDuration = options.BreakDuration,

                // Exposes live state to /api/resilience/state. Without it the breaker is a
                // black box that can only be inferred from the shape of its failures.
                StateProvider = stateProvider,

                // The manual override. Isolate holds the circuit open regardless of health -
                // the kill switch for taking a dependency out of rotation during an incident
                // without a deploy. Reset closes it and clears the window, which is what an
                // operator wants after fixing the dependency rather than waiting out a break.
                //
                // Isolate is the half worth having in production: automatic breakers react to
                // failures that already happened, and sometimes you know a dependency is about
                // to be unavailable before it starts failing.
                ManualControl = manualControl,

                OnOpened = args =>
                {
                    log.Record(
                        "circuit-breaker",
                        "opened",
                        $"breakDuration={args.BreakDuration.TotalSeconds:0.##}s " +
                        $"trigger={Describe(args.Outcome)}");
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    log.Record(
                        "circuit-breaker",
                        "half-opened",
                        "break elapsed; admitting one trial call");
                    return default;
                },
                OnClosed = args =>
                {
                    log.Record(
                        "circuit-breaker",
                        "closed",
                        args.IsManual ? "manual reset" : "trial call succeeded; traffic restored");
                    return default;
                }
            })

            // -------------------------------------------------------------------------------
            // [5] ATTEMPT TIMEOUT - innermost, bounds one network call.
            //
            // Inside the breaker on purpose: a timeout is a failure the breaker must count. A
            // dependency that has stopped answering is exactly the case the breaker exists for,
            // and it is invisible if timeouts are swallowed outside the counting.
            //
            // HttpClient.Timeout is left infinite so this is the only thing bounding an
            // attempt. Two competing timeouts produce two different exception types for one
            // condition.
            // -------------------------------------------------------------------------------
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = options.AttemptTimeout,
                OnTimeout = args =>
                {
                    log.CountTimeout();
                    log.Record("timeout", "attempt-elapsed", $"after {args.Timeout.TotalSeconds:0.##}s");
                    return default;
                }
            });
    }

    /// <summary>
    /// Is this outcome worth retrying, and does it say anything about the dependency's health?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both retry and the breaker ask the same question, so they share one answer. If they
    /// disagreed, the pipeline could retry failures the breaker ignores and never trip, or trip
    /// on failures it refuses to retry.
    /// </para>
    /// <para>
    /// 4xx is deliberately excluded, apart from 408 and 429. A 400 or a 404 means the request
    /// was wrong, and it will be exactly as wrong the second time; retrying wastes a call and,
    /// worse, counting it would let one client's malformed requests open the breaker for
    /// everybody. 429 is included because the server is explicitly asking for a pause, which is
    /// what backoff provides.
    /// </para>
    /// </remarks>
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

    private static string Describe(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is not null
            ? outcome.Exception.GetType().Name
            : $"HTTP {(int?)outcome.Result?.StatusCode}";
}
