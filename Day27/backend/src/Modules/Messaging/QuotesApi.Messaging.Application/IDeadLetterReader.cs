namespace QuotesApi.Messaging.Application;

public sealed record DeadLetterEntry(
    string MessageId,
    string? Reason,
    string? Description,
    int DeliveryCount,
    DateTimeOffset EnqueuedAt,
    string Body);

public interface IDeadLetterReader
{
    /// <summary>Reads without removing, so inspecting the DLQ does not drain it.</summary>
    Task<IReadOnlyList<DeadLetterEntry>> PeekAsync(
        string subscriptionName, int maxMessages, CancellationToken cancellationToken);

    /// <summary>
    /// Receives and completes dead-lettered messages, permanently discarding them.
    /// </summary>
    /// <remarks>
    /// Exists so verification runs start from a clean slate. In a real system the equivalent
    /// operation is a deliberate one — you fix the cause, then replay or drop, and either way
    /// somebody decides.
    /// </remarks>
    Task<int> PurgeAsync(string subscriptionName, CancellationToken cancellationToken);
}
