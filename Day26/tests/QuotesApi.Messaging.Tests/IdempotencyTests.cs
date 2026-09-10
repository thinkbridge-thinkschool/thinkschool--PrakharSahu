using Microsoft.Extensions.Logging.Abstractions;
using QuotesApi.Messaging;

namespace QuotesApi.Messaging.Tests;

/// <summary>
/// Covers the idempotency rules that make at-least-once delivery survivable.
/// </summary>
/// <remarks>
/// These run without a broker. What they pin down is the logic that decides whether a
/// redelivered message is re-processed — which is the part that fails silently in production
/// if it is wrong, because nothing errors: the queue drains and the work simply does not
/// happen, or happens twice.
/// </remarks>
public sealed class IdempotencyTests
{
    private static InMemoryProcessedMessageTracker NewTracker() =>
        new(NullLogger<InMemoryProcessedMessageTracker>.Instance);

    [Fact]
    public void The_first_delivery_is_processed()
    {
        var tracker = NewTracker();
        Assert.True(tracker.TryBeginProcessing("audit", "msg-1"));
    }

    [Fact]
    public void A_redelivery_of_the_same_message_is_suppressed()
    {
        var tracker = NewTracker();

        Assert.True(tracker.TryBeginProcessing("audit", "msg-1"));
        // Service Bus is at-least-once: a consumer that completed its work and died before
        // settling will see this message again. The handler must not run twice.
        Assert.False(tracker.TryBeginProcessing("audit", "msg-1"));
        Assert.Equal(1, tracker.DuplicatesSuppressed("audit"));
    }

    /// <summary>
    /// The one that matters most, and the easiest to get wrong.
    /// </summary>
    [Fact]
    public void The_same_MessageId_on_a_different_subscription_is_NOT_a_duplicate()
    {
        var tracker = NewTracker();

        // A topic fans one message out to every subscription, and each copy carries the same
        // MessageId. Dedupe on the id alone and whichever subscription reads first wins: the
        // audit reader processes it, the search indexer calls it a duplicate and skips it, and
        // the index is silently, permanently incomplete. Nothing errors. Nothing retries.
        Assert.True(tracker.TryBeginProcessing("audit", "msg-1"));
        Assert.True(tracker.TryBeginProcessing("search-index", "msg-1"));

        Assert.Equal(0, tracker.DuplicatesSuppressed("search-index"));
        Assert.Equal(1, tracker.ProcessedCount("audit"));
        Assert.Equal(1, tracker.ProcessedCount("search-index"));
    }

    [Fact]
    public void Releasing_a_failed_message_lets_the_retry_run()
    {
        var tracker = NewTracker();

        Assert.True(tracker.TryBeginProcessing("audit", "msg-1"));

        // The handler threw. Without this the redelivery — and every retry after it, all the
        // way to the dead-letter queue — is skipped as a duplicate and completed as though it
        // had succeeded. The message would drain having done nothing.
        tracker.Release("audit", "msg-1");

        Assert.True(tracker.TryBeginProcessing("audit", "msg-1"));
    }

    [Fact]
    public void A_message_with_no_id_is_allowed_through_rather_than_dropped()
    {
        var tracker = NewTracker();

        // Nothing to dedupe on. Processing twice is recoverable; never processing is not, so
        // the ambiguity is resolved toward doing the work.
        Assert.True(tracker.TryBeginProcessing("audit", ""));
        Assert.True(tracker.TryBeginProcessing("audit", ""));
    }

    /// <summary>
    /// The competing-consumer race: two consumers, one message, one winner.
    /// </summary>
    [Fact]
    public async Task Only_one_of_many_concurrent_consumers_claims_a_message()
    {
        var tracker = NewTracker();
        const int consumers = 32;

        using var start = new SemaphoreSlim(0, consumers);

        var attempts = Enumerable.Range(0, consumers).Select(_ => Task.Run(async () =>
        {
            await start.WaitAsync();
            return tracker.TryBeginProcessing("audit", "contended");
        })).ToArray();

        start.Release(consumers);
        var results = await Task.WhenAll(attempts);

        // ConcurrentDictionary.TryAdd is the claim and it is atomic. Checking ContainsKey and
        // then adding would leave a window where several consumers all see "not processed" and
        // all run the handler — which is precisely the race a competing-consumer setup creates
        // and this class exists to close.
        Assert.Equal(1, results.Count(claimed => claimed));
        Assert.Equal(consumers - 1, tracker.DuplicatesSuppressed("audit"));
    }

    [Fact]
    public void Counters_are_reported_per_subscription()
    {
        var tracker = NewTracker();

        tracker.TryBeginProcessing("audit", "a");
        tracker.TryBeginProcessing("audit", "a");         // duplicate on audit
        tracker.TryBeginProcessing("search-index", "a");  // first on search-index
        tracker.TryBeginProcessing("search-index", "b");

        Assert.Equal(1, tracker.DuplicatesSuppressed("audit"));
        Assert.Equal(0, tracker.DuplicatesSuppressed("search-index"));
        Assert.Equal(1, tracker.ProcessedCount("audit"));
        Assert.Equal(2, tracker.ProcessedCount("search-index"));
    }
}
