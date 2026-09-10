using System.Net;
using Polly;
using Polly.CircuitBreaker;
using QuotesApi.Resilience;

namespace QuotesApi.Resilience.Tests;

/// <summary>
/// Builds the real production pipeline in front of a dependency the test controls.
/// </summary>
/// <remarks>
/// <para>
/// The strategies, their order and their predicates come from
/// <see cref="UpstreamResilience.Configure"/> — the same method Program.cs calls. Only the
/// numbers change, and only downward, so a breaker cycle takes a second instead of fifteen. A
/// test that rebuilt the pipeline itself would pass happily while the shipped one was ordered
/// wrong, which is the one bug it most needs to catch.
/// </para>
/// <para>
/// The dependency is a delegate rather than an HTTP server: the timings under test are
/// milliseconds apart, and a real socket adds enough jitter to make them flaky. The live proof
/// in <c>scripts/verify-resilience.sh</c> covers the real-network half.
/// </para>
/// </remarks>
internal sealed class PipelineHarness
{
    private int _attempts;

    public PipelineHarness(Action<UpstreamOptions>? tune = null)
    {
        Options = new UpstreamOptions
        {
            // Polly enforces a 500ms floor on both breaker windows, so these are as small as
            // the library allows. Everything else is small enough to keep the suite quick.
            SamplingDuration = TimeSpan.FromSeconds(1),
            BreakDuration = TimeSpan.FromMilliseconds(500),
            FailureRatio = 0.5,

            // Deliberately unreachable by default, which holds the breaker out of the way.
            //
            // Not a convenience — it was a bug the first time this suite ran. At a realistic
            // threshold of 2, retrying one 500 four times produces four breaker samples, the
            // breaker opens on the second, and the third attempt is short-circuited. The retry
            // tests then measured the breaker instead of retry and reported 2 attempts where 4
            // were configured. Tests that name one strategy now exercise one strategy; the
            // interaction between them has a test of its own, which is where it belongs.
            MinimumThroughput = 100,
            MaxRetryAttempts = 3,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            AttemptTimeout = TimeSpan.FromMilliseconds(200),
            TotalTimeout = TimeSpan.FromSeconds(5),
            MaxConcurrency = 4,
            MaxQueue = 2
        };

        tune?.Invoke(Options);

        Log = new ResilienceEventLog();
        StateProvider = new CircuitBreakerStateProvider();
        ManualControl = new CircuitBreakerManualControl();

        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        UpstreamResilience.Configure(builder, Options, Log, StateProvider, ManualControl);
        Pipeline = builder.Build();
    }

    public UpstreamOptions Options { get; }
    public ResilienceEventLog Log { get; }
    public CircuitBreakerStateProvider StateProvider { get; }
    public CircuitBreakerManualControl ManualControl { get; }
    public ResiliencePipeline<HttpResponseMessage> Pipeline { get; }

    /// <summary>Total callback invocations — one per attempt, so retries are countable.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    public CircuitState State => StateProvider.CircuitState;

    /// <summary>Runs one operation through the pipeline, declaring whether it is safe to repeat.</summary>
    public async Task<Outcome<HttpResponseMessage>> ExecuteAsync(
        bool idempotent,
        Func<CancellationToken, Task<HttpResponseMessage>> dependency,
        CancellationToken cancellationToken = default)
    {
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(UpstreamResilience.IsIdempotent, idempotent);

        try
        {
            var response = await Pipeline.ExecuteAsync(
                async (ctx, state) =>
                {
                    Interlocked.Increment(ref _attempts);
                    return await state(ctx.CancellationToken);
                },
                context,
                dependency);

            return Outcome.FromResult(response);
        }
        catch (Exception ex)
        {
            return Outcome.FromException<HttpResponseMessage>(ex);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>Shorthand for a dependency that always answers with one status code.</summary>
    public Task<Outcome<HttpResponseMessage>> ExecuteAsync(bool idempotent, HttpStatusCode status) =>
        ExecuteAsync(idempotent, _ => Task.FromResult(new HttpResponseMessage(status)));

    public void ResetAttempts() => Interlocked.Exchange(ref _attempts, 0);

    /// <summary>
    /// Drives enough failures to satisfy MinimumThroughput and the failure ratio.
    /// </summary>
    /// <remarks>
    /// Retry is deliberately switched off for these calls (idempotent: false). With it on, one
    /// call would produce four breaker samples and the count would depend on retry internals
    /// rather than on the caller — which makes the throughput arithmetic in the tests unreadable.
    /// </remarks>
    public async Task DriveToOpenAsync()
    {
        for (var i = 0; i < Options.MinimumThroughput * 3 && State == CircuitState.Closed; i++)
        {
            await ExecuteAsync(idempotent: false, HttpStatusCode.InternalServerError);
        }
    }

    /// <summary>A harness whose breaker is live, for the tests that are about the breaker.</summary>
    public static PipelineHarness WithLiveBreaker(Action<UpstreamOptions>? tune = null) =>
        new(o =>
        {
            o.MinimumThroughput = 2;
            tune?.Invoke(o);
        });

    public IReadOnlyList<string> TransitionSequence() =>
        Log.StateTransitions()
            .Where(e => e.Event is "opened" or "half-opened" or "closed")
            .Select(e => e.Event)
            .ToArray();
}
