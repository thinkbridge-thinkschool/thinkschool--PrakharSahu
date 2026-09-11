using System.Net;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using QuotesApi.Resilience;

namespace QuotesApi.Resilience.Tests;

/// <summary>
/// One test per promise the pipeline makes. Each drives the real strategies rather than
/// asserting on how they were configured, because a configuration assertion passes just as
/// happily when the strategies are composed in the wrong order.
/// </summary>
public class ResiliencePipelineTests
{
    // =====================================================================================
    // RETRY — and the idempotency rule that governs it.
    // =====================================================================================

    [Fact]
    public async Task Idempotent_call_is_retried_up_to_the_configured_limit()
    {
        var harness = new PipelineHarness();

        var outcome = await harness.ExecuteAsync(idempotent: true, HttpStatusCode.InternalServerError);

        // One original attempt plus MaxRetryAttempts retries.
        Assert.Equal(harness.Options.MaxRetryAttempts + 1, harness.Attempts);
        Assert.Equal(HttpStatusCode.InternalServerError, outcome.Result?.StatusCode);
        Assert.Equal(harness.Options.MaxRetryAttempts, harness.Log.Retries);
    }

    [Fact]
    public async Task Non_idempotent_call_is_never_retried()
    {
        var harness = new PipelineHarness();

        await harness.ExecuteAsync(idempotent: false, HttpStatusCode.InternalServerError);

        // The heart of the day. Identical failure, identical pipeline; the only difference is
        // the caller's declaration that repeating this would have side effects. A POST that
        // charges a card must fail once, not four times.
        Assert.Equal(1, harness.Attempts);
        Assert.Equal(0, harness.Log.Retries);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Client_errors_are_not_retried_even_when_idempotent(HttpStatusCode status)
    {
        var harness = new PipelineHarness();

        await harness.ExecuteAsync(idempotent: true, status);

        // The request was wrong, and it will be exactly as wrong next time. Retrying burns
        // capacity to arrive at the same answer more slowly.
        Assert.Equal(1, harness.Attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Retryable_4xx_codes_are_retried(HttpStatusCode status)
    {
        var harness = new PipelineHarness();

        await harness.ExecuteAsync(idempotent: true, status);

        // The two 4xx codes that describe a temporary condition rather than a bad request.
        // 429 in particular is the server asking for a pause, which is what backoff provides.
        Assert.Equal(harness.Options.MaxRetryAttempts + 1, harness.Attempts);
    }

    [Fact]
    public async Task Retry_stops_as_soon_as_the_dependency_recovers()
    {
        var harness = new PipelineHarness();
        var call = 0;

        var outcome = await harness.ExecuteAsync(idempotent: true, _ =>
        {
            call++;
            return Task.FromResult(new HttpResponseMessage(
                call < 3 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK));
        });

        Assert.Equal(HttpStatusCode.OK, outcome.Result?.StatusCode);
        Assert.Equal(3, harness.Attempts);
        Assert.Equal(2, harness.Log.Retries);
    }

    // =====================================================================================
    // CIRCUIT BREAKER — the closed / open / half-open / closed cycle the exercise asks for.
    // =====================================================================================

    [Fact]
    public async Task Breaker_starts_closed()
    {
        var harness = PipelineHarness.WithLiveBreaker();

        Assert.Equal(CircuitState.Closed, harness.State);
    }

    [Fact]
    public async Task Breaker_opens_under_sustained_failure()
    {
        var harness = PipelineHarness.WithLiveBreaker();

        await harness.DriveToOpenAsync();

        Assert.Equal(CircuitState.Open, harness.State);
        Assert.Contains(
            harness.Log.StateTransitions(),
            e => e.Event == "opened");
    }

    [Fact]
    public async Task Open_breaker_rejects_without_calling_the_dependency()
    {
        var harness = PipelineHarness.WithLiveBreaker();
        await harness.DriveToOpenAsync();

        harness.ResetAttempts();
        var outcome = await harness.ExecuteAsync(idempotent: true, HttpStatusCode.OK);

        // The dependency would have answered 200 if asked. It was not asked — and that is the
        // whole value of the breaker: the failing dependency gets a rest, and the caller gets
        // its answer in microseconds instead of after four attempts and three backoffs.
        Assert.Equal(0, harness.Attempts);
        Assert.IsType<BrokenCircuitException>(outcome.Exception);
    }

    [Fact]
    public async Task Open_breaker_stops_retry_from_hammering_the_dependency()
    {
        var harness = PipelineHarness.WithLiveBreaker();
        await harness.DriveToOpenAsync();

        harness.ResetAttempts();
        var retriesBefore = harness.Log.Retries;
        await harness.ExecuteAsync(idempotent: true, HttpStatusCode.InternalServerError);

        // Retry sits outside the breaker, so it sees BrokenCircuitException. That is not a
        // transient outcome, so retry declines it and the call fails immediately. Without this
        // cooperation an open breaker would still cost four rejections per call.
        Assert.Equal(0, harness.Attempts);
        Assert.Equal(retriesBefore, harness.Log.Retries);
    }

    [Fact]
    public async Task Breaker_half_opens_after_the_break_duration_and_closes_on_success()
    {
        var harness = PipelineHarness.WithLiveBreaker();
        await harness.DriveToOpenAsync();
        Assert.Equal(CircuitState.Open, harness.State);

        // Wait out the break. A margin is added because the transition is observed on the next
        // call, not fired by a timer, and sleeping exactly to the boundary races it.
        await Task.Delay(harness.Options.BreakDuration + TimeSpan.FromMilliseconds(250));

        harness.ResetAttempts();
        var outcome = await harness.ExecuteAsync(idempotent: false, HttpStatusCode.OK);

        // One trial call was admitted, it succeeded, and the breaker closed. No operator
        // involved, and the dependency was probed once rather than stampeded.
        Assert.Equal(1, harness.Attempts);
        Assert.Equal(HttpStatusCode.OK, outcome.Result?.StatusCode);
        Assert.Equal(CircuitState.Closed, harness.State);

        Assert.Equal(
            new[] { "opened", "half-opened", "closed" },
            harness.TransitionSequence());
    }

    [Fact]
    public async Task Breaker_reopens_when_the_trial_call_fails()
    {
        var harness = PipelineHarness.WithLiveBreaker();
        await harness.DriveToOpenAsync();

        await Task.Delay(harness.Options.BreakDuration + TimeSpan.FromMilliseconds(250));

        var outcome = await harness.ExecuteAsync(idempotent: false, HttpStatusCode.InternalServerError);

        // Recovery is not assumed on a schedule. One failed probe and the breaker is open for
        // another full break duration, so a dependency that is still down is not re-flooded the
        // instant its timer expires.
        Assert.Equal(HttpStatusCode.InternalServerError, outcome.Result?.StatusCode);
        Assert.Equal(CircuitState.Open, harness.State);

        Assert.Equal(
            new[] { "opened", "half-opened", "opened" },
            harness.TransitionSequence());
    }

    [Fact]
    public async Task Client_errors_never_open_the_breaker()
    {
        var harness = PipelineHarness.WithLiveBreaker();

        for (var i = 0; i < harness.Options.MinimumThroughput * 3; i++)
        {
            await harness.ExecuteAsync(idempotent: false, HttpStatusCode.BadRequest);
        }

        // A 400 says the caller is wrong, not that the dependency is unhealthy. If these were
        // counted, one client sending malformed requests could take the dependency away from
        // every other caller in the process.
        Assert.Equal(CircuitState.Closed, harness.State);
    }

    [Fact]
    public async Task Breaker_does_not_open_below_minimum_throughput()
    {
        var harness = PipelineHarness.WithLiveBreaker(o => o.MinimumThroughput = 10);

        await harness.ExecuteAsync(idempotent: false, HttpStatusCode.InternalServerError);

        // 1 failure out of 1 call is a 100% failure ratio. Without the throughput floor a quiet
        // service would trip its own breaker on a single unlucky request.
        Assert.Equal(CircuitState.Closed, harness.State);
    }

    // =====================================================================================
    // TIMEOUTS — attempt and total.
    // =====================================================================================

    [Fact]
    public async Task Attempt_timeout_bounds_a_single_call()
    {
        var harness = new PipelineHarness(o => o.AttemptTimeout = TimeSpan.FromMilliseconds(100));

        var outcome = await harness.ExecuteAsync(idempotent: false, async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Assert.IsType<TimeoutRejectedException>(outcome.Exception);
        Assert.True(harness.Log.Timeouts >= 1);
    }

    [Fact]
    public async Task Attempt_timeout_is_retried_like_any_other_transient_failure()
    {
        var harness = new PipelineHarness(o => o.AttemptTimeout = TimeSpan.FromMilliseconds(60));

        var outcome = await harness.ExecuteAsync(idempotent: true, async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // A hung socket is the textbook transient fault, so it is retried.
        Assert.IsType<TimeoutRejectedException>(outcome.Exception);
        Assert.Equal(harness.Options.MaxRetryAttempts + 1, harness.Attempts);
    }

    [Fact]
    public async Task Attempt_timeout_counts_against_the_breaker()
    {
        var harness = PipelineHarness.WithLiveBreaker(
            o => o.AttemptTimeout = TimeSpan.FromMilliseconds(60));

        for (var i = 0; i < 4 && harness.State == CircuitState.Closed; i++)
        {
            await harness.ExecuteAsync(idempotent: false, async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        }

        // The attempt timeout sits INSIDE the breaker precisely so this holds. A dependency
        // that has stopped answering is the case the breaker exists for, and it would be
        // invisible if timeouts expired outside the counting.
        Assert.Equal(CircuitState.Open, harness.State);
    }

    [Fact]
    public async Task Breaker_opening_mid_retry_cuts_the_remaining_attempts_short()
    {
        var harness = PipelineHarness.WithLiveBreaker();

        var outcome = await harness.ExecuteAsync(idempotent: true, HttpStatusCode.InternalServerError);

        // Three retries were configured; fewer than four attempts were made. Each attempt is a
        // breaker sample, so the breaker tripped part-way through the retry sequence and the
        // rest were short-circuited.
        //
        // Worth stating as a test rather than discovering in production: a retry count is a
        // ceiling, not a promise. Tune MinimumThroughput below MaxRetryAttempts + 1 and a single
        // caller can open the breaker on its own retries, taking the dependency away from every
        // other caller in the process. The shipped defaults keep the threshold above one
        // caller's full retry sequence for exactly this reason.
        Assert.True(
            harness.Attempts < harness.Options.MaxRetryAttempts + 1,
            $"expected the breaker to cut the sequence short, but all {harness.Attempts} attempts ran");
        Assert.Equal(CircuitState.Open, harness.State);
        Assert.IsType<BrokenCircuitException>(outcome.Exception);
    }

    [Fact]
    public async Task Total_timeout_bounds_the_whole_operation_including_retries()
    {
        var harness = new PipelineHarness(o =>
        {
            o.AttemptTimeout = TimeSpan.FromSeconds(2);
            o.TotalTimeout = TimeSpan.FromMilliseconds(400);
            o.MaxRetryAttempts = 10;
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(50);
        });

        var started = DateTimeOffset.UtcNow;
        var outcome = await harness.ExecuteAsync(idempotent: true, async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var elapsed = DateTimeOffset.UtcNow - started;

        // Eleven attempts at two seconds each would be twenty-two seconds. The caller was
        // promised 400ms and got 400ms: whatever the pipeline does internally is its own
        // business, not the caller's wait.
        Assert.IsType<TimeoutRejectedException>(outcome.Exception);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(2),
            $"total timeout did not bound the operation; it took {elapsed.TotalMilliseconds:0}ms");
    }

    // =====================================================================================
    // BULKHEAD.
    // =====================================================================================

    [Fact]
    public async Task Bulkhead_rejects_once_permits_and_queue_are_exhausted()
    {
        var harness = new PipelineHarness(o =>
        {
            o.MaxConcurrency = 2;
            o.MaxQueue = 1;
            o.AttemptTimeout = TimeSpan.FromSeconds(5);
            o.TotalTimeout = TimeSpan.FromSeconds(10);
        });

        using var release = new SemaphoreSlim(0);

        // Two permits plus one queue slot = three calls accommodated, the rest shed.
        var calls = Enumerable.Range(0, 8).Select(_ => harness.ExecuteAsync(
            idempotent: false,
            async ct =>
            {
                await release.WaitAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            })).ToArray();

        // Give the limiter a moment to admit and reject before anything is allowed to finish.
        await Task.Delay(300);
        release.Release(8);

        var outcomes = await Task.WhenAll(calls);
        var rejected = outcomes.Count(o => o.Exception is RateLimiterRejectedException);
        var admitted = outcomes.Count(o => o.Result is not null);

        Assert.True(rejected > 0, "the bulkhead admitted every call; nothing was shed");
        Assert.Equal(8, rejected + admitted);

        // Shedding is not a side effect to be tolerated, it is the mechanism. Eight callers
        // against a dependency sized for two means five of them must be told "no" quickly
        // instead of all eight waiting and none of them succeeding in time.
        Assert.True(admitted <= harness.Options.MaxConcurrency + harness.Options.MaxQueue);
        Assert.Equal(admitted, harness.Attempts);
    }

    [Fact]
    public async Task Bulkhead_lets_calls_through_again_once_slots_free_up()
    {
        var harness = new PipelineHarness(o =>
        {
            o.MaxConcurrency = 1;
            o.MaxQueue = 0;
        });

        var first = await harness.ExecuteAsync(idempotent: false, HttpStatusCode.OK);
        var second = await harness.ExecuteAsync(idempotent: false, HttpStatusCode.OK);

        // The limiter caps concurrency, not throughput. Sequential callers all get through.
        Assert.Equal(HttpStatusCode.OK, first.Result?.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.Result?.StatusCode);
    }

    // =====================================================================================
    // CLASSIFICATION — the two predicates the whole pipeline hangs off.
    // =====================================================================================

    [Theory]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    [InlineData("PUT", true)]
    [InlineData("DELETE", true)]
    [InlineData("OPTIONS", true)]
    [InlineData("POST", false)]
    [InlineData("PATCH", false)]
    public void Http_methods_are_classified_per_rfc_9110(string method, bool expected)
    {
        Assert.Equal(expected, UpstreamClient.IsIdempotent(new HttpMethod(method)));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public void Status_codes_are_classified_as_transient_or_permanent(HttpStatusCode status, bool expected)
    {
        var outcome = Polly.Outcome.FromResult(new HttpResponseMessage(status));

        Assert.Equal(expected, UpstreamResilience.IsTransient(outcome));
    }

    [Fact]
    public void Transport_failures_are_transient()
    {
        Assert.True(UpstreamResilience.IsTransient(
            Polly.Outcome.FromException<HttpResponseMessage>(new HttpRequestException("reset"))));

        Assert.True(UpstreamResilience.IsTransient(
            Polly.Outcome.FromException<HttpResponseMessage>(new TimeoutRejectedException())));

        // A bug in our own code is not the dependency's fault. Counting it would let a
        // NullReferenceException on this side open a breaker on a perfectly healthy service.
        Assert.False(UpstreamResilience.IsTransient(
            Polly.Outcome.FromException<HttpResponseMessage>(new InvalidOperationException())));
    }
}
