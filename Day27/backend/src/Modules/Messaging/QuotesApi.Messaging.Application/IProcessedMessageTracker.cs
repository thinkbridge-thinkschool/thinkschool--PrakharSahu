namespace QuotesApi.Messaging.Application;

public interface IProcessedMessageTracker
{
    /// <summary>
    /// Claims a message for processing. Returns <c>false</c> if this subscription has already
    /// handled it, in which case the handler must be skipped and the message completed.
    /// </summary>
    bool TryBeginProcessing(string subscriptionName, string messageId);

    /// <summary>Releases a claim so a failed message can be retried on redelivery.</summary>
    void Release(string subscriptionName, string messageId);

    /// <summary>How many duplicates this subscription has suppressed. Proof, and a metric.</summary>
    int DuplicatesSuppressed(string subscriptionName);

    int ProcessedCount(string subscriptionName);
}
