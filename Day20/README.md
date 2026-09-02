# Day 20 — The outbox pattern

A domain change and its event, committed in **one EF transaction**, with a relay that publishes
afterwards and marks the row sent. Proven by killing the process mid-flight and watching the
message still arrive.

**The deliverable is [EXERCISE.md](EXERCISE.md)** — the outbox table, the relay, and the crash
scenarios.

Built on Day 19. **Day 18 and Day 19 are unchanged**; everything here is a copy plus the outbox.

## The proof, in one paragraph

```
POST /api/quotes  →  BEGIN TRANSACTION
                       INSERT Quotes          ← the domain change
                       INSERT OutboxMessages  ← the event, same transaction
                     COMMIT
                                    ↓  (nothing published yet — deliberately)
                     OutboxRelay: claim → publish → mark ProcessedAt
```

Kill the process anywhere after `COMMIT` and the event is still sitting in the table. That is the
entire guarantee.

## Results

| | |
|---|---|
| `tests/QuotesApi.Outbox.Tests` | **8/8** — both crash points, outage, retry, lease |
| All three suites | **23/23** (Outbox 8 · Messaging 7 · Jobs 8) |
| `scripts/verify-outbox.sh` | **10/10** — live `taskkill /F` and restart |

Captured run: [`docs/outbox-verification.txt`](docs/outbox-verification.txt).

## Layout

```
Day20/
├── EXERCISE.md                       the deliverable
├── backend/
│   ├── Models/OutboxMessage.cs           ◀ the outbox table
│   ├── Data/AppDbContext.cs              EF config + IX_Outbox_Pending (filtered)
│   ├── Outbox/
│   │   ├── OutboxWriter.cs               stages the row — deliberately does NOT save
│   │   ├── OutboxRelay.cs                ◀ the relay: claim → publish → mark
│   │   └── OutboxFaults.cs               crash injection, Development only
│   ├── Endpoints/OutboxEndpoints.cs
│   └── Extensions/
│       ├── OutboxExtensions.cs           DI
│       └── QuoteEndpointExtensions.cs    ◀ the BeginTransactionAsync block
├── tests/QuotesApi.Outbox.Tests/     crash scenarios (SQLite, real transactions)
├── scripts/verify-outbox.sh          live crash proof
└── docs/outbox-verification.txt      its captured output
```

Day 19's messaging (topic, two subscriptions, competing consumers, DLQ) is carried over intact —
the relay publishes through the same `IEventPublisher`.

## Endpoints

| Method | Route | Auth | Purpose |
|---|---|---|---|
| `GET` | `/api/outbox` | anonymous | `pending` / `processed` counts plus recent rows |
| `GET` | `/api/outbox/{id}` | anonymous | one row — for asserting across a restart |
| `POST` | `/api/outbox/faults` | required | arm a relay crash. **Development only** |
| `GET` | `/api/outbox/faults` | anonymous | what is currently armed |

`pending` is the number that matters. In a healthy system it hovers near zero; a rising value
means the relay is down or the broker is refusing, and it is visible here long before anyone
notices a stale projection downstream.

Fault modes: `BeforePublish` · `AfterPublishBeforeMark` · `PublishThrows` · `None`.

## Running it

```bash
# Crash scenarios — deterministic, no broker, no process killing
cd Day20/tests/QuotesApi.Outbox.Tests && dotnet test      # 8/8

# The live proof: taskkill /F mid-flight, then restart
cd Day20 && bash scripts/verify-outbox.sh                  # 10/10
```

By hand:

```bash
cd backend
Jwt__Key="$(openssl rand -base64 48)" dotnet run           # :5267

# arm a crash, create a quote, watch the row stay Pending
curl -X POST localhost:5267/api/outbox/faults -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"mode":"BeforePublish","occurrences":50}'

curl -X POST localhost:5267/api/quotes -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"author":"Ada Lovelace","text":"Something quotable."}'

curl -s localhost:5267/api/outbox | jq          # pending: 1
# kill the process, restart it, then:
curl -s localhost:5267/api/outbox | jq          # pending: 0 — it published on restart
```

If a run leaves an orphan holding the DLL, note that `dotnet exec` runs as **`dotnet`**, not
`QuotesApi` — kill by command line:

```powershell
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
  Where-Object { $_.CommandLine -like '*QuotesApi.dll*' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

## Configuration

```json
"Outbox": {
  "Enabled": true,
  "PollInterval": "00:00:02",     // wait between sweeps when the outbox is empty
  "BatchSize": 20,                // a full batch skips the wait, so backlogs drain
  "LeaseDuration": "00:00:30",    // must exceed the worst realistic publish time
  "MaxAttempts": 5,
  "RetryBackoff": "00:00:02"      // base for exponential backoff
}
```

Messaging works exactly as in Day 19 — an empty `ServiceBus:ConnectionString` disables it and the
relay publishes through a no-op, so the outbox mechanics can be exercised with no broker at all.

## Note on the database

The outbox table only appears in databases created **after** it was added, because startup uses
`EnsureCreated()` rather than `Migrate()`. Use a fresh database file for Day 20 — the long-lived
dev `quotes.db` will not gain the table.
