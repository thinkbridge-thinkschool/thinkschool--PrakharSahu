namespace QuotesApi.Resilience;

/// <summary>
/// Tuning for the outbound dependency and the resilience pipeline in front of it.
/// </summary>
/// <remarks>
/// Every default here is chosen so the breaker's full lifecycle — closed, open, half-open,
/// closed again — completes in seconds rather than minutes. Production values would be larger
/// on every axis; a demo whose breaker takes a minute to open teaches nothing because nobody
/// watches long enough to see the recovery.
/// </remarks>
public sealed class UpstreamOptions
{
    public const string SectionName = "Upstream";

    /// <summary>Base address of the outbound dependency.</summary>
    /// <remarks>
    /// Points at this same application by default. The fake upstream lives under
    /// <c>/upstream/*</c> so failures can be switched on and off at will, but the call is a
    /// genuine HTTP request over a real socket — real status codes, real timeouts, real
    /// connection behaviour. A hand-written fake <c>HttpMessageHandler</c> would prove the
    /// pipeline is wired up and nothing about how it behaves against a network.
    ///
    /// Left empty by default and resolved from the addresses Kestrel actually bound, rather
    /// than hard-coded to the launch profile's port. Every previous day's verification script
    /// picks its own free port, and a hard-coded one would make the outbound call land on
    /// whatever else happens to be listening there — or on nothing at all.
    /// </remarks>
    public string BaseAddress { get; set; } = string.Empty;

    // ---- Timeout ------------------------------------------------------------------------
    /// <summary>Bounds ONE attempt. Innermost strategy.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Bounds the whole operation including every retry. Outermost timeout.</summary>
    /// <remarks>
    /// Must exceed <see cref="AttemptTimeout"/> × (<see cref="MaxRetryAttempts"/> + 1) plus the
    /// backoff, or the total timeout fires mid-retry and the retries were pointless. Getting
    /// this relationship wrong is the most common way a "resilient" client ends up slower and
    /// no more reliable.
    /// </remarks>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(10);

    // ---- Retry --------------------------------------------------------------------------
    public int MaxRetryAttempts { get; set; } = 3;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    // ---- Circuit breaker ----------------------------------------------------------------
    /// <summary>Fraction of failures within the sampling window that trips the breaker.</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>The rolling window the ratio is measured over.</summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Calls required in the window before the ratio means anything.
    /// </summary>
    /// <remarks>
    /// Without a floor, one failed call out of one is a 100% failure ratio and the breaker trips
    /// on a single blip. This is the setting that stops a quiet service from breaking itself.
    /// </remarks>
    public int MinimumThroughput { get; set; } = 4;

    /// <summary>How long the breaker stays open before allowing one trial call.</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(5);

    // ---- Bulkhead (concurrency limiter) --------------------------------------------------
    /// <summary>Maximum concurrent calls allowed through to the dependency.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>How many may wait for a slot before callers are rejected outright.</summary>
    /// <remarks>
    /// A queue of zero would reject the instant all slots are busy, which is too brittle for
    /// normal jitter. An unbounded queue is worse — it converts a downstream slowdown into
    /// unbounded memory growth and turns rejection into timeout, which is the same outage with
    /// a longer fuse.
    /// </remarks>
    public int MaxQueue { get; set; } = 2;
}
