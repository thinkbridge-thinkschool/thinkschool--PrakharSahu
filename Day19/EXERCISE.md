# Day 19 — Azure Service Bus topics + DLQ

> **Exercise:** Paste the publisher + consumer, the idempotency key handling, and proof a
> poison message landed in the DLQ.

**Repository:** `<GITHUB_LINK_PLACEHOLDER>` — `Day19/`

---

## 1. The publisher

`backend/Messaging/EventPublisher.cs`. Publishes to a **topic**, not a queue — that is the
whole difference. A queue gives each message to one consumer; a topic copies it to every
subscription, so one `quote.created` event reaches both the audit reader and the search
indexer, and neither knows the other exists.

```csharp
public sealed class ServiceBusEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly ServiceBusSender _sender;

    public ServiceBusEventPublisher(ServiceBusClient client, IOptions<ServiceBusOptions> options, ...)
    {
        // Created once and kept. ServiceBusClient owns an AMQP connection, and a sender per
        // publish would open and tear down a connection per HTTP request — the classic way to
        // make a fast broker look slow.
        _sender = client.CreateSender(options.Value.TopicName);
    }

    public async Task<string> PublishAsync(QuoteEvent @event, CancellationToken cancellationToken)
    {
        var message = new ServiceBusMessage(JsonSerializer.Serialize(@event))
        {
            // THE IDEMPOTENCY KEY, carried by the transport.
            //
            // Set explicitly from the event rather than left to the SDK, which would generate
            // a fresh Guid per send. If a publish times out and is retried, the broker may
            // already hold the first copy — with a stable MessageId the consumer recognises
            // the second delivery as the same event. With a generated one it does the work
            // twice and dedupe is decorative.
            MessageId   = @event.EventId,
            ContentType = "application/json",
            Subject     = @event.EventType,

            // Readable by subscription filters WITHOUT deserialising the body, which is how a
            // rule can route on event type cheaply.
            ApplicationProperties =
            {
                ["eventType"] = @event.EventType,
                ["quoteId"]   = @event.QuoteId,
                ["poison"]    = @event.Poison
            }
        };

        await _sender.SendMessageAsync(message, cancellationToken);
        return message.MessageId;
    }
}
```

Real application traffic goes through it too — `POST /api/quotes` publishes a `quote.created`
event whose `EventId` is derived from the quote id (`quote-created-{id}`), not random, so a
client retrying a timed-out create produces the same event id rather than a second event.

## 2. The consumer

`backend/Messaging/SubscriptionWorker.cs`. One `BackgroundService` hosting
**`ConsumersPerSubscription` × 2 subscriptions** independent processors, each handling
`MaxConcurrentCalls` messages at once.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    foreach (var subscription in new[] { _options.AuditSubscription, _options.SearchIndexSubscription })
    {
        for (var i = 1; i <= _options.ConsumersPerSubscription; i++)
        {
            var consumerId = $"{subscription}#{i}";

            var processor = _client.CreateProcessor(_options.TopicName, subscription,
                new ServiceBusProcessorOptions
                {
                    MaxConcurrentCalls = _options.MaxConcurrentCalls,

                    // Manual settlement, deliberately. With the default the SDK completes on
                    // return and abandons on throw — which sounds right and quietly removes
                    // the ability to distinguish "retry this" from "this will never work,
                    // dead-letter it now". Those two decisions are the substance of the DLQ
                    // story, so this class makes them explicitly.
                    AutoCompleteMessages = false,

                    // Keeps the lock alive while a slow handler runs. Without it a handler
                    // outliving its lock loses the message to another consumer mid-flight and
                    // the work happens twice.
                    MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(2)
                });

            processor.ProcessMessageAsync += args => OnMessageAsync(subscription, consumerId, args);
            processor.ProcessErrorAsync   += args => OnErrorAsync(consumerId, args);
            await processor.StartProcessingAsync(stoppingToken);
        }
    }
    await Task.Delay(Timeout.Infinite, stoppingToken);   // the processors own their own pumps
}
```

**Competing consumers**: they all pull from the same subscription and the broker hands each
message to exactly one of them. No coordination, no partitioning, no leader election.
Throughput scales by adding consumers; correctness does not depend on how many there are. In
production these would be separate replicas — running several in one process is what lets a
laptop *show* that they do not double-process.

The message handler, which is where all three decisions live:

```csharp
private async Task OnMessageAsync(string subscription, string consumerId, ProcessMessageEventArgs args)
{
    // 1. Can it be understood at all?
    //    A payload this consumer cannot parse will not parse on the next delivery either.
    //    Retrying it three times wastes two deliveries and buries the real reason under
    //    "MaxDeliveryCountExceeded". Reject it now, with the actual cause.
    QuoteEvent? @event;
    try { @event = JsonSerializer.Deserialize<QuoteEvent>(args.Message.Body.ToString()); }
    catch (JsonException failure)
    {
        await args.DeadLetterMessageAsync(args.Message,
            deadLetterReason: "MalformedPayload",
            deadLetterErrorDescription: failure.Message);
        return;
    }

    // 2. Have we already done this, on THIS subscription?
    //    Complete rather than abandon — the work is done, redelivering achieves nothing.
    if (!_tracker.TryBeginProcessing(subscription, args.Message.MessageId))
    {
        await args.CompleteMessageAsync(args.Message);
        return;
    }

    // 3. Do the work.
    try
    {
        using var scope = _scopeFactory.CreateScope();   // scoped handler per message
        var handler = scope.ServiceProvider.GetServices<ISubscriptionHandler>()
            .First(h => h.SubscriptionName == subscription);

        await handler.HandleAsync(@event, args.CancellationToken);
        await args.CompleteMessageAsync(args.Message);
    }
    catch (Exception failure)
    {
        // The claim MUST go back before abandoning, or the redelivery is suppressed as a
        // duplicate and completed without ever running — the message would "succeed" having
        // done nothing, and never reach the dead-letter queue.
        _tracker.Release(subscription, args.Message.MessageId);

        // Abandon, not DeadLetter. The broker counts deliveries and moves the message itself
        // once MaxDeliveryCount is exceeded, stamping DeadLetterReason=MaxDeliveryCountExceeded.
        // Dead-lettering by hand here would rob a transient fault of its remaining retries.
        await args.AbandonMessageAsync(args.Message);
    }
}
```

## 3. Idempotency key handling

`backend/Messaging/ProcessedMessageTracker.cs`.

**Why it is needed:** Service Bus is *at-least-once*. A consumer that completes its work and
dies before settling will see that message again; so will one whose lock expires mid-handler.
Redelivery is normal operation, not an error, and the only defence is a handler safe to run
twice.

```csharp
private static string Key(string subscription, string messageId) => $"{subscription}::{messageId}";

public bool TryBeginProcessing(string subscriptionName, string messageId)
{
    if (string.IsNullOrWhiteSpace(messageId))
    {
        // Nothing to dedupe on. Let it through — processed twice is recoverable,
        // never processed is not.
        return true;
    }

    // TryAdd IS the claim, and it is atomic. ContainsKey-then-Add leaves a window where two
    // competing consumers both see "not processed" and both run the handler — the precise
    // race this class exists to close.
    if (_processed.TryAdd(Key(subscriptionName, messageId), DateTimeOffset.UtcNow))
    {
        return true;
    }

    _duplicates.AddOrUpdate(subscriptionName, 1, (_, count) => count + 1);
    return false;
}

public void Release(string subscriptionName, string messageId) =>
    _processed.TryRemove(Key(subscriptionName, messageId), out _);
```

### The key is `(subscription, messageId)` — not `messageId` alone

This is the bug worth stating loudly. A topic fans one message out to every subscription, and
**each copy carries the same `MessageId`**. Dedupe on the id by itself and whichever
subscription reads first wins: the audit reader processes the event, the search indexer sees a
"duplicate" and skips it, and half the system silently stops working.

Nothing errors. The queue drains. The index is simply always missing whatever audit happened
to reach first.

Pinned by a test:

```csharp
[Fact]
public void The_same_MessageId_on_a_different_subscription_is_NOT_a_duplicate()
{
    Assert.True(tracker.TryBeginProcessing("audit",        "msg-1"));
    Assert.True(tracker.TryBeginProcessing("search-index", "msg-1"));
    Assert.Equal(0, tracker.DuplicatesSuppressed("search-index"));
}
```

And the competing-consumer race, 32 threads on one message:

```csharp
var results = await Task.WhenAll(/* 32 concurrent TryBeginProcessing("audit", "contended") */);
Assert.Equal(1,  results.Count(claimed => claimed));
Assert.Equal(31, tracker.DuplicatesSuppressed("audit"));
```

### Broker-side duplicate detection is deliberately OFF

`RequiresDuplicateDetection: false` in the emulator config, even though today is about
idempotency. Broker dedupe drops a message whose `MessageId` was seen in the last 20 seconds —
it protects against a *publisher* retrying a send, and nothing else. It does **not** protect
against the case that matters: a consumer that did the work and died before settling, whose
message is redelivered minutes later. Only the consumer can defend against that. Turning it on
would mask the behaviour being demonstrated without solving it.

## 4. Proof a poison message landed in the DLQ

<!-- VERIFICATION_OUTPUT -->

---

## What did you learn?

**A topic is not a queue with extra steps — it changes who owns delivery state.** Each
subscription has its own copy of every message, its own delivery counts, and its own
dead-letter queue. A message that poisons the audit reader dead-letters in audit's DLQ while
the search indexer completes the same event normally. One broken consumer cannot stall the
others, and that independence is the entire reason to pay for a topic.

**Idempotency is a property of the key, not of the handler.** The handlers here were already
naturally idempotent — indexing the same quote twice overwrites. The bug that would have
shipped was in the *key*: dedupe on `MessageId` alone and the second subscription silently
stops working, with no error anywhere. The failure would have been discovered by someone
noticing the search index was incomplete, weeks later.

**Abandon and dead-letter are different decisions and belong to different failures.** A
transient fault deserves its remaining retries; an unparseable payload deserves none. Letting
the SDK auto-settle removes the ability to make that distinction, which is why
`AutoCompleteMessages = false` is not a detail.

**Releasing the dedupe claim on failure is load-bearing.** Forget it and the retry is
suppressed as a duplicate and completed as though it succeeded — the message drains having
done nothing, and never reaches the DLQ. That is a silent data-loss bug hiding inside a
correctness feature.

## What would break this?

**The dedupe store is in-process and per-replica.** Two replicas each keep their own set, so a
message redelivered to a different replica is processed again. Everything demonstrated here
holds for one instance and quietly stops holding at two. The real fix is to dedupe in the
database, in the same transaction as the work — insert the message id into a `processed_messages`
table with a unique constraint and let the constraint violation be the duplicate signal.

**The publish is a dual write.** `POST /api/quotes` commits the row, then sends the message, as
two operations with no transaction across them. A crash in between loses the event while the
quote survives, and the projections are permanently stale for that quote with nothing to
detect it. The fix is the outbox pattern: write the event to the same database in the same
transaction, and publish from there with a relay. Not implemented here, and the code says so
where it happens.

**The projections are in memory.** A restart empties the audit log and the search index while
the messages that built them are already completed and gone. Real sinks are durable, and
rebuilding one means replaying from a retained source — which the topic alone does not give
you, since a completed message is gone from the subscription.

**`MaxDeliveryCount` is a guess about the failure.** Three retries is right for a blip and
useless for a downstream service that is down for ten minutes — the message dead-letters and a
human has to replay it. Real systems pair a higher count with exponential backoff, or move the
message to a retry topic with a scheduled redelivery instead of burning attempts immediately.

**A poison message can still take out throughput before it dead-letters.** With
`MaxConcurrentCalls = 2` and a flood of poison messages, both slots spend their time failing
and retrying while good messages queue behind them. Dead-lettering bounds the damage, it does
not eliminate it.

**Ordering is not preserved.** Competing consumers process concurrently, so `quote.created` and
a later `quote.deleted` for the same quote can be handled out of order. Nothing here depends on
order; the moment something does, it needs sessions (partitioned by quote id), and sessions
give up the competing-consumer parallelism that makes this fast.
