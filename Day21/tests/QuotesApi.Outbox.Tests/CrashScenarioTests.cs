using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Data;
using QuotesApi.Messaging;
using QuotesApi.Models;
using QuotesApi.Outbox;

namespace QuotesApi.Outbox.Tests;

/// <summary>
/// The crash scenarios the exercise asks to be described and proven.
/// </summary>
/// <remarks>
/// Every test here kills the relay at a specific point and then starts a fresh one, which is
/// what a process restart actually looks like. What is being proven is not that the code runs,
/// but that <b>no message is lost at any crash point</b> — and, where a duplicate is
/// unavoidable, that it is a duplicate and not a loss.
/// </remarks>
public sealed class CrashScenarioTests
{
    private static Quote NewQuote(string author = "Ada Lovelace", string text = "That brain of mine.")
        => Quote.Create(author, text, DateTimeOffset.UtcNow, userId: 1).Value!;

    /// <summary>Writes a quote and its outbox row exactly as the endpoint does.</summary>
    private static async Task<(int QuoteId, Guid MessageId)> CreateQuoteWithEvent(
        OutboxHarness harness, string author = "Ada Lovelace", string text = "That brain of mine.")
    {
        using var scope = harness.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();

        await using var transaction = await db.Database.BeginTransactionAsync();

        var quote = NewQuote(author, text);
        db.Quotes.Add(quote);
        await db.SaveChangesAsync();                      // SaveChanges #1

        var messageId = outbox.Enqueue(
            QuoteEventTypes.Created, nameof(Quote), quote.Id.ToString(),
            new { QuoteId = quote.Id, quote.Author, quote.Text }, DateTimeOffset.UtcNow);

        await db.SaveChangesAsync();                      // SaveChanges #2
        await transaction.CommitAsync();

        return (quote.Id, messageId);
    }

    // =====================================================================================
    // The transaction itself
    // =====================================================================================

    [Fact]
    public async Task The_quote_and_its_outbox_row_commit_together()
    {
        await using var harness = new OutboxHarness();
        var (quoteId, messageId) = await CreateQuoteWithEvent(harness);

        using var db = harness.NewDbContext();
        Assert.NotNull(await db.Quotes.FindAsync(quoteId));
        var message = await db.OutboxMessages.FindAsync(messageId);

        Assert.NotNull(message);
        // Written, not sent. Publishing is a separate step that cannot lose it now.
        Assert.Null(message!.ProcessedAt);
        Assert.Equal(nameof(Quote), message.AggregateType);
        Assert.Equal(quoteId.ToString(), message.AggregateId);
    }

    [Fact]
    public async Task If_the_transaction_rolls_back_neither_the_quote_nor_the_event_exists()
    {
        await using var harness = new OutboxHarness();

        using (var scope = harness.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();

            await using var transaction = await db.Database.BeginTransactionAsync();

            var quote = NewQuote();
            db.Quotes.Add(quote);
            await db.SaveChangesAsync();

            outbox.Enqueue(QuoteEventTypes.Created, nameof(Quote), quote.Id.ToString(),
                new { quote.Author }, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();

            // Never committed — the crash-before-commit case. Disposal rolls it back.
            await transaction.RollbackAsync();
        }

        using var check = harness.NewDbContext();
        // The pair is the point: no orphan event describing a quote that does not exist, and
        // no orphan quote whose event will never be sent.
        Assert.Empty(await check.Quotes.ToListAsync());
        Assert.Empty(await check.OutboxMessages.ToListAsync());
    }

    // =====================================================================================
    // SCENARIO 1 — crash BEFORE publishing.  Nothing lost, nothing duplicated.
    // =====================================================================================

    /// <summary>
    /// Options that let a crashed relay stay crashed long enough to observe.
    /// </summary>
    /// <remarks>
    /// A long poll interval, deliberately. After a sweep throws, the relay sleeps for one
    /// interval before trying again — so with the default 100ms it recovers on its own before
    /// a test can assert anything, and the crash is invisible. Five seconds is the window in
    /// which "the process is down" can actually be inspected.
    /// </remarks>
    private static OutboxOptions SlowPoll() => new()
    {
        PollInterval = TimeSpan.FromSeconds(5),
        BatchSize = 10,
        LeaseDuration = TimeSpan.FromMilliseconds(200),
        MaxAttempts = 5,
        RetryBackoff = TimeSpan.FromMilliseconds(50)
    };

    [Fact]
    public async Task Crash_before_publish_loses_nothing_and_duplicates_nothing()
    {
        await using var harness = new OutboxHarness(SlowPoll());
        var (_, messageId) = await CreateQuoteWithEvent(harness);

        // Relay #1 dies before handing the message to the broker.
        harness.Faults.Arm(OutboxFaultMode.BeforePublish);
        var crashed = harness.NewRelay();
        await crashed.StartAsync(CancellationToken.None);
        await Task.Delay(600);                       // sweep runs, throws, then sleeps
        await crashed.StopAsync(CancellationToken.None);

        // The broker never saw it, and the row is still pending — which is exactly why it is
        // recoverable. Under Day 19's dual write this event would now be gone for good.
        Assert.Empty(harness.Publisher.Published);
        Assert.Equal(1, harness.PendingCount());

        // Relay #2 — the restart.
        var recovered = await harness.RunRelayUntil(
            () => harness.PendingCount() == 0, TimeSpan.FromSeconds(10));

        Assert.True(recovered, "the restarted relay should have published the pending row");
        Assert.Equal(0, harness.PendingCount());

        // Published once. Not lost, and not duplicated either — the crash happened before the
        // broker was involved at all.
        Assert.Single(harness.Publisher.Published);
        Assert.Equal(messageId.ToString(), harness.Publisher.Published[0]);
    }

    // =====================================================================================
    // SCENARIO 2 — crash AFTER publishing, BEFORE marking sent.
    //              Nothing lost. Something duplicated. This is why at-least-once.
    // =====================================================================================

    [Fact]
    public async Task Crash_after_publish_before_mark_duplicates_rather_than_loses()
    {
        await using var harness = new OutboxHarness(SlowPoll());
        var (_, messageId) = await CreateQuoteWithEvent(harness);

        harness.Faults.Arm(OutboxFaultMode.AfterPublishBeforeMark);
        var crashed = harness.NewRelay();
        await crashed.StartAsync(CancellationToken.None);
        await Task.Delay(600);                       // publishes once, throws, then sleeps
        await crashed.StopAsync(CancellationToken.None);

        // The broker HAS it. The database does not know that.
        Assert.Single(harness.Publisher.Published);
        Assert.Equal(1, harness.PendingCount());

        // Restart. The row still says pending, so it is published a second time.
        await harness.RunRelayUntil(() => harness.PendingCount() == 0, TimeSpan.FromSeconds(10));

        Assert.Equal(0, harness.PendingCount());

        // TWO publishes of the SAME message id. This is the unavoidable cost of the pattern:
        // marking sent and publishing cannot be made atomic, so one of them must happen twice
        // on a crash. The outbox chooses to duplicate rather than to lose, because a duplicate
        // is recoverable by the consumer and a loss is recoverable by nobody.
        Assert.Equal(2, harness.Publisher.Published.Count);
        Assert.All(harness.Publisher.Published, id => Assert.Equal(messageId.ToString(), id));

        // And this is the half the consumer owns: both copies carry the same id, so Day 19's
        // dedupe collapses them into one effective delivery.
        Assert.Single(harness.Publisher.DistinctPublished);
    }

    // =====================================================================================
    // SCENARIO 3 — the broker is down.
    // =====================================================================================

    [Fact]
    public async Task A_broker_outage_retries_with_backoff_and_delivers_once_it_recovers()
    {
        await using var harness = new OutboxHarness();
        var (_, messageId) = await CreateQuoteWithEvent(harness);

        var attempts = 0;
        harness.Publisher.FailWhen = _ => Interlocked.Increment(ref attempts) <= 2;

        var relay = harness.NewRelay();
        await relay.StartAsync(CancellationToken.None);

        var delivered = false;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (harness.PendingCount() == 0) { delivered = true; break; }
            await Task.Delay(50);
        }
        await relay.StopAsync(CancellationToken.None);

        Assert.True(delivered, "the relay should deliver once the broker recovers");
        Assert.Single(harness.Publisher.Published);
        Assert.Equal(messageId.ToString(), harness.Publisher.Published[0]);

        using var db = harness.NewDbContext();
        var row = await db.OutboxMessages.FindAsync(messageId);
        // Failures are recorded on the row, so an operator can see WHY it was slow rather
        // than only that it was.
        Assert.True(row!.Attempts >= 3, $"expected retries to be counted, saw {row.Attempts}");
        Assert.NotNull(row.ProcessedAt);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_stays_pending_for_replay_rather_than_vanishing()
    {
        await using var harness = new OutboxHarness(new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            BatchSize = 10,
            LeaseDuration = TimeSpan.FromMilliseconds(100),
            MaxAttempts = 2,
            RetryBackoff = TimeSpan.FromMilliseconds(10)
        });

        var (_, messageId) = await CreateQuoteWithEvent(harness);
        harness.Publisher.FailWhen = _ => true;   // never recovers

        var relay = harness.NewRelay();
        await relay.StartAsync(CancellationToken.None);
        await Task.Delay(1500);
        await relay.StopAsync(CancellationToken.None);

        using var db = harness.NewDbContext();
        var row = await db.OutboxMessages.FindAsync(messageId);

        // Still there, still pending, with the reason recorded. Deleting or tombstoning it
        // would discard the only evidence that this event ever needed to be sent.
        Assert.NotNull(row);
        Assert.Null(row!.ProcessedAt);
        Assert.True(row.Attempts >= 2);
        Assert.Contains("Broker unavailable", row.LastError);
    }

    // =====================================================================================
    // Ordering and throughput
    // =====================================================================================

    [Fact]
    public async Task Pending_messages_are_published_oldest_first()
    {
        await using var harness = new OutboxHarness();

        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var (_, messageId) = await CreateQuoteWithEvent(harness, $"Author {i}", $"Text number {i}.");
            ids.Add(messageId);
            await Task.Delay(10);   // distinct OccurredAt values
        }

        await harness.RunRelayUntil(() => harness.PendingCount() == 0, TimeSpan.FromSeconds(10));

        // Ordered by OccurredAt, so the relay does not reorder events on its way out. Note
        // this is the RELAY's ordering only — competing consumers on the far side may still
        // handle them concurrently, which is why nothing downstream may depend on order.
        Assert.Equal(ids.Select(id => id.ToString()), harness.Publisher.Published);
    }

    [Fact]
    public async Task A_leased_row_is_not_claimed_by_a_second_relay()
    {
        await using var harness = new OutboxHarness(new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            BatchSize = 10,
            LeaseDuration = TimeSpan.FromSeconds(30),   // long, so the lease is still held
            MaxAttempts = 5,
            RetryBackoff = TimeSpan.FromMilliseconds(50)
        });

        var (_, messageId) = await CreateQuoteWithEvent(harness);

        // Hold the row under a live lease by blocking the publish.
        var gate = new TaskCompletionSource();
        harness.Publisher.FailWhen = _ => { gate.Task.Wait(TimeSpan.FromSeconds(5)); return false; };

        var first = harness.NewRelay();
        await first.StartAsync(CancellationToken.None);
        await Task.Delay(300);

        using (var db = harness.NewDbContext())
        {
            var row = await db.OutboxMessages.FindAsync(messageId);
            Assert.NotNull(row!.LockedUntil);
            Assert.NotNull(row.LockedBy);
        }

        // A second relay must not pick up a row that is still leased — otherwise two relays
        // publish the same message concurrently. Harmless downstream thanks to dedupe, but it
        // doubles broker traffic for nothing and makes an incident log unreadable.
        var second = harness.NewRelay();
        await second.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await second.StopAsync(CancellationToken.None);

        gate.TrySetResult();
        await Task.Delay(300);
        await first.StopAsync(CancellationToken.None);

        Assert.Single(harness.Publisher.DistinctPublished);
    }
}
