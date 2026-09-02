# Day 20 — The outbox pattern

> **Exercise:** Paste the outbox table + relay. Describe the crash scenario you tested and why
> no message is lost or duplicated (at-least-once + idempotent consumer).

**Repository:** `<GITHUB_LINK_PLACEHOLDER>` — `Day20/`

**Results:** 8/8 outbox tests · 23/23 across all suites · **10/10 live crash proof**
(`docs/outbox-verification.txt`).

---

## The problem being fixed

Day 19 ended with a dual write, and said so in its own code:

```csharp
await repo.AddAsync(quote, ct);        // committed
await publisher.PublishAsync(evt, ct); // separate operation, no transaction across them
```

Crash between those two lines and the quote exists while the event never happened — the
projections are permanently stale for that quote and **nothing anywhere detects it**. Reversing
the order only moves the failure: publish first, crash before the commit, and consumers act on a
quote that does not exist.

There is exactly one thing you can make atomic with a database write, and that is **another
database write**.

## 1. The outbox table

`backend/Models/OutboxMessage.cs`, configured in `backend/Data/AppDbContext.cs`.

```csharp
public sealed class OutboxMessage
{
    // Identity of the event AND the consumer's idempotency key. Generated once, when the row
    // is written — so every republish after a crash carries the SAME value, which is what
    // lets a consumer recognise the duplicate. Generate it at publish time instead and each
    // retry looks like a brand-new event.
    public Guid Id { get; set; }

    public required string Type { get; set; }            // "quote.created"

    // Deliberately NOT a foreign key to Quotes — see below.
    public required string AggregateType { get; set; }
    public required string AggregateId { get; set; }

    public required string Payload { get; set; }

    public DateTime OccurredAt { get; set; }
    public DateTime? ProcessedAt { get; set; }           // null == pending. The whole definition.
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    public DateTime? LockedUntil { get; set; }           // a lease, not a lock
    public string? LockedBy { get; set; }
    public DateTime? NextAttemptAt { get; set; }         // backoff
}
```

```csharp
entity.Property(m => m.Id).ValueGeneratedNever();   // app-generated; it is the MessageId

// The relay's only hot query is "the oldest unpublished rows". A filtered index on exactly
// that keeps it proportional to the BACKLOG rather than to the table — which matters because
// the table grows forever while the backlog should hover near zero. Without the filter the
// scan gets slower every day the system works correctly.
entity.HasIndex(m => new { m.ProcessedAt, m.NextAttemptAt })
      .HasDatabaseName("IX_Outbox_Pending")
      .HasFilter("\"ProcessedAt\" IS NULL");

entity.HasIndex(m => new { m.AggregateType, m.AggregateId })
      .HasDatabaseName("IX_Outbox_Aggregate");
```

### Two decisions in that table worth defending

**No FK to `Quotes`.** An outbox row must outlive its aggregate. If a quote is hard-deleted
before the relay drains, a real FK would either block the delete or cascade the unsent event out
of existence — and the event describing a deletion is precisely the one you cannot afford to
lose. The relationship is by value: recorded, indexed, joinable when both rows exist, under no
obligation to be. The honest cost is that the database will not enforce that it points at
anything real.

**`DateTime`, not `DateTimeOffset`.** Forced by the provider, and it cost a real bug (§5).
SQLite has no date type; EF stores `DateTimeOffset` as TEXT *including the offset*, so comparing
two of them is a lexicographic string comparison that is wrong once offsets differ. EF refuses
to translate it rather than emit a query that quietly returns the wrong rows.

## 2. The transactional write

`backend/Extensions/QuoteEndpointExtensions.cs` — `POST /api/quotes`:

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);

await repo.AddAsync(result.Value!, ct);        // SaveChanges #1, inside the transaction

var messageId = outbox.Enqueue(
    type:          QuoteEventTypes.Created,
    aggregateType: nameof(Quote),
    aggregateId:   result.Value!.Id.ToString(),
    payload:       new { QuoteId = result.Value!.Id, result.Value!.Author, result.Value!.Text,
                         result.Value!.CreatedAt },
    occurredAt:    result.Value!.CreatedAt);

await db.SaveChangesAsync(ct);                 // SaveChanges #2, same transaction
await transaction.CommitAsync(ct);
```

An **explicit** transaction because the quote's id is database-generated, so the outbox row
cannot be staged until the insert has produced one. Two `SaveChanges` calls, one transaction —
both commit or neither does.

Past that line both rows are durable and **nothing has been published**. That is the point:
publishing is now a separate, retryable step that cannot lose the event, because the event is
already written down.

### The most important line of `OutboxWriter` is the one that isn't there

```csharp
public Guid Enqueue(...)
{
    var message = new OutboxMessage { Id = Guid.NewGuid(), /* … */ };
    _db.OutboxMessages.Add(message);   // Add. NOT SaveChangesAsync.
    return message.Id;
}
```

That omission is the entire pattern. Atomicity comes from the domain change and the outbox row
being tracked by the same `DbContext` and flushed by the same `SaveChangesAsync`. Saving here
would break it in a way that still passes every test that does not kill the process: the outbox
row would commit in its own transaction, and a failure in the caller's later save would leave an
event describing a quote that was never created. That is the dual write again, merely reversed.

It is registered **scoped** for the same reason — it must share the request's `DbContext`
instance, or the two writes land in different transactions and the guarantee evaporates
silently.

## 3. The relay

`backend/Outbox/OutboxRelay.cs`. **Claim → publish → mark**, and the order *is* the guarantee.

```csharp
// CLAIM — its own transaction, a time-bound lease rather than a lock, so a relay that dies
// holding claims releases them by doing nothing at all.
var claimable = await db.OutboxMessages
    .Where(m => m.ProcessedAt == null
                && (m.NextAttemptAt ?? DateTime.MinValue) <= now
                && (m.LockedUntil   ?? DateTime.MinValue) <= now
                && m.Attempts < maxAttempts)
    .OrderBy(m => m.OccurredAt)          // oldest first
    .Take(_options.BatchSize)
    .ToListAsync(cancellationToken);

foreach (var m in claimable) { m.LockedUntil = lease; m.LockedBy = _instanceId; }
await db.SaveChangesAsync(cancellationToken);

foreach (var message in claimable)
{
    try
    {
        // PUBLISH — the row id IS the MessageId, so a republish after a crash reuses it and
        // the duplicate is recognisable downstream.
        await publisher.PublishAsync(new QuoteEvent { EventId = message.Id.ToString(), /* … */ },
                                     cancellationToken);

        // MARK — only now. This is the commit point of the whole pattern.
        message.ProcessedAt = DateTime.UtcNow;
        message.LockedUntil = null;
        message.LockedBy    = null;
        message.Attempts++;
    }
    catch (Exception failure)
    {
        message.Attempts++;
        message.LastError = failure.Message;
        message.LockedUntil = null;
        // Exponential backoff. Retrying a broker outage every two seconds is how a transient
        // failure becomes a self-inflicted denial of service.
        message.NextAttemptAt = DateTime.UtcNow.Add(
            TimeSpan.FromMilliseconds(_options.RetryBackoff.TotalMilliseconds
                                      * Math.Pow(2, message.Attempts - 1)));
    }
}

await db.SaveChangesAsync(cancellationToken);
```

**Why mark last.** Marking before publishing would invert the risk: a crash in between loses the
message permanently while the row claims it was sent. Losing a message is unrecoverable; sending
one twice is a problem the consumer already solves. The pattern trades the unrecoverable failure
for the recoverable one, and that trade is the whole design.

## 4. The crash scenarios tested

### Live, with a real `taskkill /F`

`scripts/verify-outbox.sh` — **10/10**, full output in `docs/outbox-verification.txt`. Not fault
injection at the assertion level: it starts the API, arms a crash, creates a quote, then kills
the process outright (no shutdown handler, no graceful drain), and starts a *second process
against the same database file*.

```
--- The outbox row exists and is Pending
  pending=1 processed=0
      { "id": "2a25e439-5b6b-41fb-ad04-734293661b9a", "aggregateId": "6",
        "status": "Pending", "processedAt": null, "attempts": 0,
        "lockedBy": "BRAVO-15:25060" }
  [PASS] event is durably written but NOT yet published
  [PASS] log confirms the quote and the outbox row were staged in one transaction

--- taskkill /F
  [PASS] process 1 is dead — no StopAsync ran, nothing drained

--- Start a NEW process against the SAME database file
  [PASS] quote 6 is still there
      { "id": "2a25e439-5b6b-41fb-ad04-734293661b9a",
        "status": "Published", "processedAt": "2026-09-02T05:51:32.382881", "attempts": 1 }
  [PASS] the pending event was published after restart — NOTHING WAS LOST
```

### Deterministically, in `tests/QuotesApi.Outbox.Tests` (8/8)

| Crash point | Outcome | Test |
|---|---|---|
| Transaction rolls back | **Neither** the quote nor the event exists — no orphan either way | `If_the_transaction_rolls_back_neither_...` |
| **Before publish** | Row still pending → republished on restart. **Not lost, not duplicated** — the broker was never involved | `Crash_before_publish_loses_nothing_and_duplicates_nothing` |
| **After publish, before mark** | Broker has it, row says pending → published **again**. **Not lost, duplicated** | `Crash_after_publish_before_mark_duplicates_rather_than_loses` |
| Broker outage | Retries with exponential backoff, delivers once it recovers, failures recorded on the row | `A_broker_outage_retries_with_backoff_...` |
| Never recovers | Stays **pending** with `LastError` — kept for replay, never discarded | `A_message_that_keeps_failing_stays_pending_for_replay_...` |
| Two relays | A leased row is not claimed twice | `A_leased_row_is_not_claimed_by_a_second_relay` |

The decisive assertion in the duplicate case:

```csharp
// TWO publishes of the SAME message id.
Assert.Equal(2, harness.Publisher.Published.Count);
Assert.All(harness.Publisher.Published, id => Assert.Equal(messageId.ToString(), id));

// And this is the half the consumer owns: both copies carry the same id, so Day 19's dedupe
// collapses them into one effective delivery.
Assert.Single(harness.Publisher.DistinctPublished);
```

## Why no message is lost or duplicated

**Not lost** — because the event is committed to the database in the same transaction as the
change that caused it. There is no window in which the quote exists and the event does not. Every
crash point after that leaves a pending row, and a pending row is a retryable instruction.

**Duplicated — yes, sometimes, and that is deliberate.** "Hand it to the broker" and "record that
we did" are two operations that cannot be made atomic. One of them must be able to happen twice.
The outbox chooses to duplicate rather than lose, because:

> a duplicate is recoverable by the consumer; a loss is recoverable by nobody.

So the guarantee is **at-least-once**, never exactly-once. The second half of the guarantee lives
in the consumer, and it is already built — Day 19 dedupes on `MessageId` keyed by
`(subscription, messageId)`:

```csharp
if (!_tracker.TryBeginProcessing(subscription, args.Message.MessageId))
{
    await args.CompleteMessageAsync(args.Message);   // already done; do not re-run the handler
    return;
}
```

The two days join at exactly one value: **the outbox row's `Id` is published as the Service Bus
`MessageId`**, and the relay reuses it on every republish. Together they give *effectively-once
processing* out of at-least-once delivery — which is the only honest way to get it.

## 5. Bugs this exercise caught

### The relay silently delivered nothing

The claim query used `x == null || x <= now` over nullable `DateTimeOffset`. It does not
translate — EF throws `could not be translated`, the loop's `catch` logged it, and the relay
**span forever without publishing anything while looking perfectly healthy from outside**.

Two fixes, because the query bug was the smaller problem:

1. `COALESCE` instead of the null-check form, and UTC `DateTime` columns so SQLite can compare
   them at all.
2. **Consecutive-failure escalation** — a relay whose every sweep throws now logs `Critical`
   after three, saying it is "delivering NOTHING". Silence was the real defect; a swallowed
   exception that repeats at the same level forever is indistinguishable from an idle queue.

### The API would not start without a broker

`IDeadLetterReader` was only registered when messaging was configured. Minimal APIs infer an
unregistered interface parameter as the **request body**, and a `DELETE` may not have one — so
route building threw at startup and *the whole application failed to boot*:

```
Body was inferred but the method does not allow inferred body parameters.
  subscription | Route (Inferred)
  reader       | Body  (Inferred)
```

The feature flag meant to make messaging optional had instead made the API unbootable without a
broker. Fixed with `DisabledDeadLetterReader`. Day 19 never hit it because its verification
always ran with messaging enabled — **this class of bug only appears in the configuration nobody
tests.**

## What would still break this

**The relay is per-database, not per-replica-safe beyond the lease.** The lease stops two relays
publishing the same row concurrently, but it is time-based: a relay that stalls past its lease
(GC pause, disk stall) can still publish a row another relay has since claimed. Harmless —
consumer dedupe absorbs it — but it is a duplicate, not an impossibility.

**Ordering is only best-effort.** The relay claims oldest-first, but competing consumers on the
far side process concurrently, so `quote.created` and a later `quote.deleted` can be handled out
of order. Nothing here depends on order; the moment something does, it needs Service Bus sessions
partitioned by aggregate id — which costs the parallelism that makes this fast.

**The outbox table grows forever.** Published rows are never deleted. That is deliberate for an
exercise (they are the audit trail), and wrong for production past a certain size. Real systems
archive or purge on a schedule, and the filtered index is what keeps the relay fast in the
meantime.

**`MaxAttempts` gives up quietly.** After five failures the row is left pending and skipped by the
claim query. It is preserved for replay, but nothing pages anyone — the only signal is the
`pending` count on `GET /api/outbox` rising and never falling. That number belongs on a dashboard
with an alert, not in an endpoint someone has to remember to check.

**SQLite is the weakest link.** One writer at a time, and the whole guarantee rests on the
database's transaction. Postgres or SQL Server would also allow `SELECT … FOR UPDATE SKIP LOCKED`
for claiming, which is strictly better than a lease: no clock dependence, no stale-claim window.
