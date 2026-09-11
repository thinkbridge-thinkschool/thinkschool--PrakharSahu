namespace QuotesApi.Resilience;

/// <summary>How the fake upstream should misbehave right now.</summary>
public enum UpstreamFaultMode
{
    /// <summary>Answers 200 immediately.</summary>
    None,

    /// <summary>Answers 500. The classic transient server fault the pipeline is built for.</summary>
    ServerError,

    /// <summary>Answers 200, eventually. Used to trip the attempt timeout and the bulkhead.</summary>
    Slow,

    /// <summary>Answers 400. A client fault — deliberately NOT retried and NOT counted by the breaker.</summary>
    BadRequest
}

/// <summary>
/// Runtime control over the fake upstream's behaviour.
/// </summary>
/// <remarks>
/// <para>
/// A plain mutable singleton rather than <c>IOptions&lt;T&gt;</c>, for the same reason Day 21's
/// <c>CacheOptions</c> is: the proof has to flip the dependency from healthy to broken and back
/// again <em>inside one process</em>, because a circuit breaker's state only means something
/// across a continuous timeline. Restarting the app to change a setting would reset the breaker
/// and destroy the very thing being measured.
/// </para>
/// <para>
/// The endpoint that writes to this is registered in Development only. A production build has
/// no way to ask its own dependency to start failing.
/// </para>
/// </remarks>
public sealed class UpstreamFaults
{
    private volatile UpstreamFaultMode _mode = UpstreamFaultMode.None;
    private int _latencyMs = 2000;

    public UpstreamFaultMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    /// <summary>Delay applied in <see cref="UpstreamFaultMode.Slow"/>.</summary>
    /// <remarks>
    /// Default is deliberately above the 1s attempt timeout, so "slow" reliably becomes
    /// "timed out" rather than "occasionally timed out", which would make the proof flaky.
    /// </remarks>
    public int LatencyMs
    {
        get => Volatile.Read(ref _latencyMs);
        set => Volatile.Write(ref _latencyMs, Math.Clamp(value, 0, 30_000));
    }

    public bool IsHealthy => _mode == UpstreamFaultMode.None;
}
