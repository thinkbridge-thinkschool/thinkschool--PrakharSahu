using Polly.CircuitBreaker;
using QuotesApi.Resilience;

namespace QuotesApi.Endpoints;

public sealed record UpstreamFaultRequest(string Mode, int? LatencyMs);

public static class ResilienceEndpoints
{
    public static void MapResilienceEndpoints(this WebApplication app)
    {
        MapFakeUpstream(app);
        MapCallers(app);
        MapObservability(app);
        MapFaultControl(app);
    }

    // =====================================================================================
    // The dependency being protected.
    //
    // It lives in this process, but the call to it is a real HTTP request over a real socket -
    // real status codes, real connection handling, a real timeout when it stops answering.
    // Mocking the handler instead would prove the pipeline is wired up and nothing about how it
    // behaves, and "behaves" is what the exercise asks to be shown.
    //
    // Deliberately outside /api, so the CallerIdentity middleware Day 17 added does not demand
    // an Entra token from the process calling itself.
    // =====================================================================================
    private static void MapFakeUpstream(WebApplication app)
    {
        var group = app.MapGroup("/upstream");

        group.MapGet("/quote-of-the-day", Respond);
        group.MapPost("/notify", Respond);

        static async Task<IResult> Respond(UpstreamFaults faults, CancellationToken cancellationToken)
        {
            switch (faults.Mode)
            {
                case UpstreamFaultMode.ServerError:
                    return Results.Json(
                        new { error = "upstream is unavailable" },
                        statusCode: StatusCodes.Status500InternalServerError);

                case UpstreamFaultMode.BadRequest:
                    // The control case for the transient-vs-permanent distinction. Nothing about
                    // a 400 improves on the second attempt, so it is neither retried nor counted
                    // against the breaker. Counting it would let one caller's malformed requests
                    // take the dependency away from everybody else.
                    return Results.Json(
                        new { error = "the request was malformed" },
                        statusCode: StatusCodes.Status400BadRequest);

                case UpstreamFaultMode.Slow:
                    // The token is honoured, so the attempt timeout actually cancels the work
                    // rather than abandoning a task that keeps running. A dependency that
                    // ignores cancellation is how a timeout stops bounding anything.
                    await Task.Delay(faults.LatencyMs, cancellationToken);
                    return Results.Ok(new { quote = "Slow, but it arrived." });

                default:
                    return Results.Ok(new { quote = "Simplicity is a great virtue." });
            }
        }
    }

    // =====================================================================================
    // The two callers. Same pipeline, same dependency, one difference: idempotency.
    // =====================================================================================
    private static void MapCallers(WebApplication app)
    {
        var group = app.MapGroup("/api/resilience");

        // GET - idempotent, so retry applies.
        group.MapGet("/call", async (UpstreamClient client, CancellationToken ct) =>
            Describe(await client.ReadAsync(ct)));

        // POST - not idempotent, so retry does not. The timeout, breaker and bulkhead still do.
        //
        // Compare the two under the same fault: the GET makes four attempts, the POST makes one.
        // That difference is the idempotency rule, visible rather than claimed.
        group.MapPost("/call-write", async (UpstreamClient client, CancellationToken ct) =>
            Describe(await client.NotifyAsync(ct)));

        static IResult Describe(UpstreamCallResult result) => Results.Json(
            new
            {
                ok = result.Ok,
                outcome = result.Outcome.ToString(),
                status = result.StatusCode,
                elapsedMs = Math.Round(result.ElapsedMs, 1),
                detail = result.Detail
            },
            // 200 only when the dependency actually answered. A pipeline that swallows every
            // failure into a cheerful 200 has not made the system resilient, it has made the
            // failure invisible - which is worse, because now nothing alerts.
            statusCode: result.Ok ? StatusCodes.Status200OK : result.StatusCode ?? 502);
    }

    // =====================================================================================
    // Observability: the breaker's state machine, read from outside.
    // =====================================================================================
    private static void MapObservability(WebApplication app)
    {
        var group = app.MapGroup("/api/resilience");

        // GET /api/resilience/state - what the breaker is doing right now.
        //
        // Read from Polly's own CircuitBreakerStateProvider, not inferred from the event log.
        // The log says what transitions happened; only the provider says what state the next
        // call will meet, and half-open in particular exists for a single call and is gone.
        group.MapGet("/state", (CircuitBreakerStateProvider breaker, UpstreamOptions options) =>
            Results.Ok(new
            {
                circuitState = breaker.CircuitState.ToString(),
                closed = breaker.CircuitState == CircuitState.Closed,
                configuration = new
                {
                    failureRatio = options.FailureRatio,
                    minimumThroughput = options.MinimumThroughput,
                    samplingDurationSeconds = options.SamplingDuration.TotalSeconds,
                    breakDurationSeconds = options.BreakDuration.TotalSeconds,
                    maxRetryAttempts = options.MaxRetryAttempts,
                    attemptTimeoutSeconds = options.AttemptTimeout.TotalSeconds,
                    totalTimeoutSeconds = options.TotalTimeout.TotalSeconds,
                    bulkheadConcurrency = options.MaxConcurrency,
                    bulkheadQueue = options.MaxQueue
                }
            }));

        // GET /api/resilience/events - what the pipeline did, in order.
        group.MapGet("/events", (ResilienceEventLog log, int? limit, bool? transitionsOnly) =>
        {
            var events = transitionsOnly == true ? log.StateTransitions() : log.Recent(limit ?? 50);

            return Results.Ok(new
            {
                count = events.Count,
                events = events.Select(e => new
                {
                    at = e.At.ToString("HH:mm:ss.fff"),
                    strategy = e.Strategy,
                    @event = e.Event,
                    detail = e.Detail
                })
            });
        });

        // GET /api/resilience/stats - the counters.
        //
        // breakerRejections is the number that shows the breaker paying for itself: those calls
        // failed in microseconds without a socket, a thread or a retry timer, while the
        // dependency was left alone to recover.
        group.MapGet("/stats", (
            ResilienceEventLog log,
            CircuitBreakerStateProvider breaker,
            UpstreamFaults faults) => Results.Ok(new
            {
                circuitState = breaker.CircuitState.ToString(),
                upstreamMode = faults.Mode.ToString(),

                calls = log.UpstreamCalls,
                upstreamFailures = log.UpstreamFailures,
                retries = log.Retries,
                timeouts = log.Timeouts,
                breakerRejections = log.BreakerRejections,
                bulkheadRejections = log.BulkheadRejections,

                note = "breakerRejections never reached the network. They are the cost the "
                     + "breaker removed, not failures it caused."
            }));

        group.MapPost("/reset", (ResilienceEventLog log) =>
        {
            log.Reset();
            return Results.Ok(new { reset = true });
        });

        // ---------------------------------------------------------------------------------
        // Manual override of the breaker.
        //
        // isolate holds the circuit open regardless of how healthy the dependency looks - the
        // kill switch for pulling a dependency out of rotation mid-incident without a deploy.
        // An automatic breaker can only react to failures that have already happened, and there
        // are times you know a dependency is about to be unavailable before it starts failing.
        //
        // close clears the window and lets traffic through again, which is what an operator
        // wants after fixing the dependency rather than waiting out a break duration.
        // ---------------------------------------------------------------------------------
        group.MapPost("/breaker/{action}", async (
            string action,
            CircuitBreakerManualControl control,
            CircuitBreakerStateProvider state,
            ResilienceEventLog log,
            CancellationToken ct) =>
        {
            switch (action.ToLowerInvariant())
            {
                case "isolate":
                    await control.IsolateAsync(ct);
                    log.Record("circuit-breaker", "isolated", "manual override; circuit held open");
                    break;

                case "close":
                    await control.CloseAsync(ct);
                    break;

                default:
                    return Results.BadRequest(new { error = "Use 'isolate' or 'close'." });
            }

            return Results.Ok(new { circuitState = state.CircuitState.ToString() });
        });
    }

    // =====================================================================================
    // Fault injection - Development only.
    //
    // This endpoint's whole purpose is to make a dependency fail on demand. In any environment
    // where the dependency is real, that is not a diagnostic, it is an outage with an HTTP API.
    // It is not registered outside Development, so it cannot be reached rather than merely
    // being discouraged.
    // =====================================================================================
    private static void MapFaultControl(WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        app.MapPost("/api/resilience/upstream/faults", (
            UpstreamFaultRequest request,
            UpstreamFaults faults,
            ILogger<UpstreamFaults> logger) =>
        {
            if (!Enum.TryParse<UpstreamFaultMode>(request.Mode, ignoreCase: true, out var mode))
            {
                return Results.BadRequest(new
                {
                    error = $"Unknown mode '{request.Mode}'.",
                    known = Enum.GetNames<UpstreamFaultMode>()
                });
            }

            faults.Mode = mode;

            if (request.LatencyMs is { } latency)
            {
                faults.LatencyMs = latency;
            }

            logger.LogWarning(
                "Upstream fault mode set to {Mode} (latency {LatencyMs}ms).", mode, faults.LatencyMs);

            return Results.Ok(new { mode = mode.ToString(), latencyMs = faults.LatencyMs });
        });
    }
}
