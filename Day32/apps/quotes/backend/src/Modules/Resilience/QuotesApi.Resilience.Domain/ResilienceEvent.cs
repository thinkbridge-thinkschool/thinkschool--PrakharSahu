namespace QuotesApi.Resilience.Domain;

/// <summary>One thing the pipeline did, with the time it did it.</summary>
public sealed record ResilienceEvent(
    DateTimeOffset At,
    string Strategy,
    string Event,
    string Detail);
