using System.Collections.Concurrent;

namespace QuotesApi.Messaging;

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

/// <summary>
/// Remembers which messages a subscription has already processed, so handlers can be treated
/// as idempotent.
/// </summary>
/// <remarks>
/// <para><b>Why this is needed at all.</b> Service Bus is at-least-once. A consumer that
/// completes its work and then dies before settling the message will see that message again;
/// so will one whose lock expires mid-handler. Redelivery is normal operation, not an error,
/// and the only defence is for the handler to be safe to run twice.</para>
///
/// <para><b>The key is (subscription, messageId) — not messageId alone.</b> This is the bug
/// worth stating loudly. A topic fans one message out to every subscription, and each copy
/// carries the <em>same</em> MessageId. Dedupe on the id by itself and whichever subscription
/// reads first wins: the audit reader processes the event, the search indexer sees a "duplicate"
/// and skips it, and half the system silently stops working. Nothing errors. The queue drains.
/// The index is simply always missing whatever audit happened to get to first.</para>
///
/// <para><b>What this implementation is not.</b> In-process and per-replica. Two replicas each
/// keep their own set, so a message redelivered to a different replica is processed again —
/// which is exactly the case a real system pushes into the database, deduping in the same
/// transaction as the work itself. See EXERCISE.md, "What would break this?".</para>
/// </remarks>
public sealed class InMemoryProcessedMessageTracker : IProcessedMessageTracker
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _processed = new();
    private readonly ConcurrentDictionary<string, int> _duplicates = new();
    private readonly ILogger<InMemoryProcessedMessageTracker> _logger;

    /// <summary>Bounded, or this dictionary is a memory leak with a respectable name.</summary>
    private const int MaxRetained = 10_000;

    public InMemoryProcessedMessageTracker(ILogger<InMemoryProcessedMessageTracker> logger) =>
        _logger = logger;

    private static string Key(string subscription, string messageId) => $"{subscription}::{messageId}";

    public bool TryBeginProcessing(string subscriptionName, string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            // No id means nothing to dedupe on. Let it through rather than silently dropping
            // it — a message processed twice is recoverable, one never processed is not.
            _logger.LogWarning(
                "Message on '{Subscription}' arrived with no MessageId; cannot dedupe it.",
                subscriptionName);
            return true;
        }

        // TryAdd is the claim, and it is atomic. Checking ContainsKey and then adding would
        // leave a window in which two competing consumers both see "not processed" and both
        // run the handler — the precise race this class exists to close.
        if (_processed.TryAdd(Key(subscriptionName, messageId), DateTimeOffset.UtcNow))
        {
            if (_processed.Count > MaxRetained) Evict();
            return true;
        }

        _duplicates.AddOrUpdate(subscriptionName, 1, (_, count) => count + 1);
        _logger.LogInformation(
            "Duplicate suppressed on '{Subscription}': MessageId {MessageId} was already processed.",
            subscriptionName, messageId);

        return false;
    }

    public void Release(string subscriptionName, string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return;

        // Called when the handler threw. The claim has to be given back, or the retry — and
        // every retry after it, all the way to the dead-letter queue — would be skipped as a
        // duplicate and the message would be completed as though it had succeeded. That
        // failure mode is silent: the queue drains, the work never happens.
        _processed.TryRemove(Key(subscriptionName, messageId), out _);
    }

    public int DuplicatesSuppressed(string subscriptionName) =>
        _duplicates.GetValueOrDefault(subscriptionName);

    public int ProcessedCount(string subscriptionName) =>
        _processed.Keys.Count(k => k.StartsWith($"{subscriptionName}::", StringComparison.Ordinal));

    private void Evict()
    {
        foreach (var key in _processed.OrderBy(pair => pair.Value).Take(MaxRetained / 4).Select(p => p.Key))
        {
            _processed.TryRemove(key, out _);
        }
    }
}
