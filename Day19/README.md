# Day 19 — Azure Service Bus topics + DLQ

Quote events published to a Service Bus **topic** with **two subscriptions**, drained by
**competing consumers**, with handlers made **idempotent on `MessageId`** and a **poison
message proven to land in the dead-letter queue**.

**The deliverable is [EXERCISE.md](EXERCISE.md)** — publisher, consumer, idempotency handling,
and the DLQ proof.

Built on Day 18's backend. **Day 18 is unchanged**; everything here is a copy plus the
messaging layer.

## The shape

```
POST /api/quotes ──┐
                   ├──► IEventPublisher ──► topic: quote-events
POST /api/messaging/publish ─┘                    │
                                                  │  every subscription gets its own copy
                        ┌─────────────────────────┴─────────────────────────┐
                        ▼                                                   ▼
              subscription: audit                              subscription: search-index
              MaxDeliveryCount 3                               MaxDeliveryCount 3
                        │                                                   │
              ┌─────────┴─────────┐                             ┌───────────┴───────────┐
              ▼                   ▼                             ▼                       ▼
        consumer audit#1    consumer audit#2            search-index#1          search-index#2
              └─────────┬─────────┘                             └───────────┬───────────┘
                        ▼                                                   ▼
              AuditProjectionHandler                            SearchIndexHandler
                        │                                                   │
                        └──── fails 3x ──► audit/$deadletterqueue           └──► search-index/$deadletterqueue
```

Four consumers, two subscriptions. The broker hands each message to exactly one consumer
*per subscription* — so both handlers run for every event, and neither runs twice.

## Files

| Path | What |
|---|---|
| `backend/Messaging/QuoteEvent.cs` | the contract. `EventId` becomes the `MessageId` |
| `backend/Messaging/EventPublisher.cs` | **publisher** → topic, plus a no-op for when messaging is off |
| `backend/Messaging/SubscriptionWorker.cs` | **consumer** — competing processors, manual settlement |
| `backend/Messaging/ProcessedMessageTracker.cs` | **idempotency**, keyed by *(subscription, messageId)* |
| `backend/Messaging/DeadLetterReader.cs` | peeks/purges `$deadletterqueue` |
| `backend/Messaging/Handlers/` | the two subscription handlers + their projections |
| `backend/Endpoints/MessagingEndpoints.cs` | publish · projections · DLQ |
| `scripts/emulator/` | compose + `Config.json` defining the topic and both subscriptions |
| `scripts/start-emulator.sh` | brings the emulator up and waits for health |
| `scripts/verify-messaging.sh` | the end-to-end proof |
| `scripts/provision-servicebus.sh` | the same topology on a real Azure namespace |
| `tests/QuotesApi.Messaging.Tests/` | idempotency tests, no broker needed |

## Endpoints

| Method | Route | Auth | Purpose |
|---|---|---|---|
| `POST` | `/api/messaging/publish` | required | publish test events — `count`, `eventId`, `poison`, `malformed` |
| `GET` | `/api/messaging/projections` | anonymous | what each subscription produced + duplicates suppressed |
| `GET` | `/api/messaging/dlq/{subscription}` | anonymous | dead-lettered messages with `DeadLetterReason` |
| `DELETE` | `/api/messaging/dlq/{subscription}` | required | drain the DLQ so a run starts clean |

`POST /api/quotes` also publishes a `quote.created` event, so the topic carries real
application traffic rather than only test messages.

## Running it

```bash
# 1. Unit tests — idempotency logic, no broker
cd Day19/tests/QuotesApi.Messaging.Tests && dotnet test

# 2. Start Microsoft's Service Bus emulator (Docker; first run pulls ~1.5GB)
cd Day19 && ./scripts/start-emulator.sh

# 3. End-to-end: fan-out, competing consumers, dedupe, retries, DLQ
./scripts/verify-messaging.sh

# 4. Tear down
./scripts/start-emulator.sh --stop
```

By hand:

```bash
export ServiceBus__ConnectionString='Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;'
cd backend && Jwt__Key="$(openssl rand -base64 48)" dotnet run
```

## Why the emulator

It is **Microsoft's own** Service Bus emulator, not a stand-in. It enforces
`MaxDeliveryCount`, moves messages to the dead-letter queue itself, and speaks the same AMQP
protocol as production. The application code cannot tell the difference — only the connection
string changes.

The alternative is a real namespace, which for **topics** means the **Standard** tier: the
Basic tier is queues-only and cannot express this exercise at all. `scripts/provision-servicebus.sh`
creates exactly the same topology in Azure whenever that is wanted.

## Configuration

```json
"ServiceBus": {
  "ConnectionString": "",            // empty disables messaging entirely
  "TopicName": "quote-events",
  "AuditSubscription": "audit",
  "SearchIndexSubscription": "search-index",
  "MaxConcurrentCalls": 2,           // concurrency within one consumer
  "ConsumersPerSubscription": 2,     // competing consumers per subscription
  "MaxDeliveryCount": 3              // mirrors the broker's setting, for logs only
}
```

An empty connection string disables messaging rather than failing startup — the same switch
Day 17 used for caller identity, so the Week-1 API and the Day 18 job tests still run on a
machine with no broker anywhere near it.

`MaxDeliveryCount` here is **for log messages only**. The real limit lives on the subscription
in Azure or in the emulator's `Config.json`; the broker counts deliveries and moves the
message, and no client setting can override that.
