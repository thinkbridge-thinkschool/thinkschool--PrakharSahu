namespace QuotesApi.Resilience.Contracts;

/// <summary>Why a call ended the way it did. The vocabulary the proof is written in.</summary>
public enum UpstreamOutcome
{
    Succeeded,

    /// <summary>The dependency answered, and its answer was a failure.</summary>
    UpstreamFailed,

    /// <summary>Rejected by the breaker without touching the network. Costs nothing.</summary>
    CircuitOpen,

    /// <summary>Rejected by the bulkhead: no slot and no room in its queue.</summary>
    BulkheadRejected,

    /// <summary>An attempt, or the whole operation, ran out of time.</summary>
    TimedOut
}

public sealed record UpstreamCallResult(
    UpstreamOutcome Outcome,
    int? StatusCode,
    double ElapsedMs,
    string Detail)
{
    public bool Ok => Outcome == UpstreamOutcome.Succeeded;
}
