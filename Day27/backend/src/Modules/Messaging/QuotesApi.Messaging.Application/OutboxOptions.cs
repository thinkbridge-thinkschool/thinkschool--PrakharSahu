namespace QuotesApi.Messaging.Application;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>How often to look for pending rows when the last sweep found nothing.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Rows claimed per sweep.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// How long a claim is held before another relay may take the row.
    /// </summary>
    /// <remarks>
    /// Must exceed the worst realistic publish time. Too short and two relays publish the same
    /// row concurrently — survivable, since the consumer dedupes, but pure waste. Too long and
    /// a crashed relay's rows sit untouched until the lease expires.
    /// </remarks>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Attempts before the row is left alone for a human.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Base for exponential backoff between attempts.</summary>
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromSeconds(2);

    public bool Enabled { get; set; } = true;
}
