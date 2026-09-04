using System.Collections.Concurrent;

namespace QuotesApi.Resilience;

/// <summary>One thing the pipeline did, with the time it did it.</summary>
public sealed record ResilienceEvent(
    DateTimeOffset At,
    string Strategy,
    string Event,
    string Detail);

/// <summary>
/// Records what the resilience pipeline actually did, so the breaker's state machine can be
/// observed rather than asserted.
/// </summary>
/// <remarks>
/// <para>
/// The exercise asks for "logs/metrics of the breaker opening then half-opening to recovery".
/// Polly v8 raises callbacks for exactly those transitions — <c>OnOpened</c>, <c>OnClosed</c>,
/// <c>OnHalfOpened</c> — and this is where they land, timestamped and ordered.
/// </para>
/// <para>
/// Why a log and not just a counter: the interesting thing about a circuit breaker is the
/// <em>sequence</em>. "Opened, then half-opened, then closed" is a different story from "opened,
/// half-opened, opened again", and a counter cannot tell them apart. Half-open in particular
/// exists for a single trial call, and it is invisible unless the transition is recorded when it
/// happens.
/// </para>
/// <para>
/// Bounded, because an unbounded list fed by every retry of every request is a memory leak with
/// a respectable name.
/// </para>
/// </remarks>
public sealed class ResilienceEventLog
{
    private readonly ConcurrentQueue<ResilienceEvent> _events = new();
    private const int MaxRetained = 300;

    // Counters are separate from the log: the log answers "what happened, in what order", the
    // counters answer "how much", and a load test needs the second without re-reading the first.
    private long _upstreamCalls;
    private long _upstreamFailures;
    private long _retries;
    private long _breakerRejections;
    private long _timeouts;
    private long _bulkheadRejections;

    public void Record(string strategy, string @event, string detail = "")
    {
        _events.Enqueue(new ResilienceEvent(DateTimeOffset.UtcNow, strategy, @event, detail));
        while (_events.Count > MaxRetained && _events.TryDequeue(out _)) { }
    }

    public void CountUpstreamCall() => Interlocked.Increment(ref _upstreamCalls);
    public void CountUpstreamFailure() => Interlocked.Increment(ref _upstreamFailures);
    public void CountRetry() => Interlocked.Increment(ref _retries);
    public void CountBreakerRejection() => Interlocked.Increment(ref _breakerRejections);
    public void CountTimeout() => Interlocked.Increment(ref _timeouts);
    public void CountBulkheadRejection() => Interlocked.Increment(ref _bulkheadRejections);

    public long UpstreamCalls => Interlocked.Read(ref _upstreamCalls);
    public long UpstreamFailures => Interlocked.Read(ref _upstreamFailures);
    public long Retries => Interlocked.Read(ref _retries);
    public long BreakerRejections => Interlocked.Read(ref _breakerRejections);
    public long Timeouts => Interlocked.Read(ref _timeouts);
    public long BulkheadRejections => Interlocked.Read(ref _bulkheadRejections);

    /// <summary>Transitions only — the story the exercise asks to be shown.</summary>
    public IReadOnlyList<ResilienceEvent> StateTransitions() =>
        _events.Where(e => e.Strategy == "circuit-breaker").ToArray();

    public IReadOnlyList<ResilienceEvent> Recent(int limit = 50) =>
        _events.TakeLast(Math.Clamp(limit, 1, MaxRetained)).ToArray();

    public void Reset()
    {
        while (_events.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _upstreamCalls, 0);
        Interlocked.Exchange(ref _upstreamFailures, 0);
        Interlocked.Exchange(ref _retries, 0);
        Interlocked.Exchange(ref _breakerRejections, 0);
        Interlocked.Exchange(ref _timeouts, 0);
        Interlocked.Exchange(ref _bulkheadRejections, 0);
    }
}
