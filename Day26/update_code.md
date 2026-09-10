# Day 26 — every file, and why

Day 22 piece 1's QuotesApi carried forward and instrumented. Most of the code is untouched; what
changed is listed here, and the reasoning for the two changes that carry the whole day is at the
top because everything else is supporting work.

---

## The two changes that matter

### `backend/Models/OutboxMessage.cs` — two nullable columns

```csharp
public string? TraceParent { get; set; }
public string? TraceState  { get; set; }
```

This is the single most important addition of the day, and the reason is that **a database row is
a boundary the async context does not cross**.

Inside the API request, `Activity` flows automatically — any span raised while handling
`POST /api/quotes` is a child of that request without anybody wiring it. But the outbox exists
precisely to *decouple* the write from the publish. The relay picks the row up later, in a
different process, on a different thread, with no ambient Activity at all.

Without these columns nothing errors. The API trace ends cleanly at "wrote the row" and a separate
orphan trace begins at "published a message", and the two are impossible to correlate afterwards.
The question the whole exercise exists to answer — *this request was slow, where did the time go?*
— becomes unanswerable at exactly the hop most likely to be responsible.

Nullable on purpose: a message enqueued by a background job legitimately has no incoming trace,
and the relay treats absence as "start a new trace" rather than as an error.

The EF configuration in `Data/AppDbContext.cs` caps them at 64 and 512 characters. A traceparent
is exactly 55 — version(2) + trace-id(32) + parent-id(16) + flags(2) + three hyphens — and
tracestate permits up to 32 vendor entries. This is telemetry metadata written on **every** domain
event; an unbounded column here must never be the reason a business write fails.

### `backend/Outbox/OutboxWriter.cs` and `OutboxRelay.cs` — capture, then restore

```csharp
// OutboxWriter — the only moment the context exists.
TraceParent = Activity.Current?.Id,
TraceState  = Activity.Current?.TraceStateString
```

`?.` and not `!`. A `NullReferenceException` on a business write, caused by telemetry, would be an
absurd trade.

```csharp
// OutboxRelay — parentId takes a STRING, which is all that survived.
using var publishActivity = string.IsNullOrEmpty(message.TraceParent)
    ? Telemetry.Outbox.StartActivity("outbox.publish", ActivityKind.Producer)
    : Telemetry.Outbox.StartActivity("outbox.publish", ActivityKind.Producer,
                                      parentId: message.TraceParent);

publishActivity?.SetTag("outbox.reparented", !string.IsNullOrEmpty(message.TraceParent));
```

The parent `Activity` is long gone — it ended when the HTTP response was written. Only its
*identity* survived, and an identity is all the linkage needs. `ActivityKind.Producer` is what
makes App Insights render this as one end of a queue hop.

The `outbox.reparented` tag exists so the claim is falsifiable. If the traces linked for some
other reason the tag would read `False`, and `query-kql.sh` asserts it reads `True` — 22 of 22.

---

## New files

### `backend/Observability/Telemetry.cs`

Every `ActivitySource` and metric instrument, in one place, because an `ActivitySource` only emits
spans if its name was registered with `AddSource(...)` at startup. A source created inline and
never registered is **not an error and produces no warning** — it silently emits nothing, and the
gap looks exactly like a propagation bug.

`SourceNames` is derived from the sources themselves rather than repeated as string literals, so a
source added above cannot be forgotten below.

It also holds the two role names. `Telemetry.WorkerRoleName` being distinct from
`ApiRoleName` is what makes the deliverable legible: App Insights groups spans on `cloud_RoleName`
to decide how many boxes to draw. One role would still stitch the trace correctly and render it as
a single box — right, and useless as evidence.

The metrics (`quotes.outbox.published`, `quotes.outbox.lag`, `quotes.messaging.consumed`) are
recorded and not yet queried. `outbox.lag` is the most useful number the outbox produces and one
no HTTP metric contains: the API returns as soon as the row commits, so request latency stays flat
while lag grows, and a stalled relay is invisible from the front door until somebody notices the
events stopped arriving.

### `infra/` — the telemetry destination

`main.bicep` provisions Log Analytics, workspace-based App Insights, a Standard Service Bus
namespace with one topic and two subscriptions, and the RBAC to reach it.

`SamplingPercentage: 100` is deliberate rather than forgotten. Adaptive sampling is the correct
default for a busy service and wrong here: a sampled-out span produces a trace with a **hole** in
it, indistinguishable from a propagation bug. The entire deliverable is "the trace stitches", so
the trace has to be complete. The 1 GB/day cap is the cost control instead — it stops ingestion
rather than silently thinning it.

`modules/alert.bicep` deploys the error-rate rule. The reasoning for a rate over a count, and for
the `| where Total > 10` guard, is in the file header and in
[EXERCISE.md](EXERCISE.md#4-error-rate-and-the-alert).

`modules/rbac.bicep` is separate for the reason Day 23 discovered: a role assignment's name and
scope must both be computable before the deployment starts or Bicep refuses with `BCP120`, and
module parameters are start-known by definition.

### `kql/*.kql` — four queries

Written in the **App Insights** schema (`requests`, `dependencies`) rather than the workspace
schema (`AppRequests`, `AppDependencies`), because they are meant to be pasted straight into the
portal Logs blade. That choice is why `query-kql.sh` has to query through the App Insights
endpoint.

`04-distributed-trace.kql` is the only one written to be capable of failing. `| where roleCount > 1`
returns nothing at all if no `operation_Id` contains spans from both roles.

### `scripts/`

`deploy.sh` provisions and writes `.env` for the local run. `run-trace.sh` starts both roles,
generates a deliberate mix of traffic, waits for the outbox to drain and lets the exporter flush.
`query-kql.sh` runs every query and asserts. `capture-trace.sh` + `render-trace.mjs` produce the
waterfall image.

`run-trace.sh` uses a `.env`, which Day 25 argued against. The argument there was about a
cloud-hosted app, where App Service injects settings and a file would be a competing source of
truth. It does not apply to a process on a laptop: there is no platform to inject anything, and
the alternative is exporting six variables by hand every time a shell opens.

---

## Modified

### `backend/Program.cs`

**Role selection.** `SERVICE_ROLE` picks `api`, `worker` or `all`, and an unrecognised value
throws rather than defaulting — a typo silently falling back to `all` would run both roles in one
process and produce a trace that looks right and proves nothing.

The api role turns its own workers off through **configuration** rather than by editing
`AddMessaging`/`AddOutbox`, because those already have exactly the switches needed and are covered
by the Day 19–22 test suites. Reaching in to add a role parameter would change three signatures
and their tests to express something the existing switches already say.

**OTel.** Every source from `Telemetry.SourceNames`, EF Core instrumentation for the DB tier, and
`WithMetrics` — which Day 11 never wired. `AddService(serviceName: roleName, serviceInstanceId:
machine:pid)` is what sets `cloud_RoleName`.

The EF Core instrumentation is left at its defaults, and the default that matters is the one *not*
changed: it can be told to attach query **parameter values** to each span, and must not be.
Parameter values are where user data lives, and attaching them exports it to a store queryable by
anyone with Reader on the workspace, retained 30 days, outside every control the application
database has.

**Schema ownership.** Only the api role calls `EnsureCreated` and seeds. See bug 2 below.

**WAL.** `PRAGMA journal_mode=WAL` and `busy_timeout=5000`, because two processes now share one
SQLite file. Under the default rollback journal a writer takes an exclusive lock over the whole
database and the two collide with `database is locked`. This is a SQLite limitation worked around,
not a design worth keeping — Day 25's Azure SQL assumes cross-process concurrency as a baseline.

### `backend/Messaging/ServiceBusOptions.cs` and `Extensions/MessagingExtensions.cs`

Adds `FullyQualifiedNamespace` and switches to token auth, because the namespace has
`disableLocalAuth: true` — Day 25's model applied to a process running on a laptop. The connection
string path is kept, last, because the Day 19–22 tests construct options directly.

`UseAzureCliCredential` exists because of bug 5. The comment in `MessagingExtensions` originally
claimed "DefaultAzureCredential works in both places with no branch"; there is now a branch, and
the comment says so rather than describing what was hoped for.

### `backend/Messaging/SubscriptionWorker.cs`

A `consume {subscription}` span per message, parented from the traceparent read off the message
itself — see bug 6 in [EXERCISE.md](EXERCISE.md#6-seven-things-that-broke). `ReadTraceParent`
checks both `Diagnostic-Id` (what the Azure SDK wrote for years) and `traceparent` (the current
name), so a queue holding messages from either era still links.

### `backend/.env`

The inherited `ConnectionStrings__Redis` is commented out. No Redis runs here, and with it
configured every cache write blocked for the full 5-second connect timeout before failing — which
turned a 20 ms POST into a 5 s one and made the latency percentiles measure a broken dependency
rather than the application.

---

## The bugs, and what each one taught

Full detail in [EXERCISE.md](EXERCISE.md#6-seven-things-that-broke). The pattern is what matters:
**every one of them produced a green-looking result while being wrong.**

| # | bug | what it looked like |
|---|---|---|
| 1 | stale Redis config | percentiles measuring a 5s timeout, not the app |
| 2 | `EnsureCreated` race between roles | `table "OutboxMessages" already exists` |
| 3 | `taskkill` silently failing in-script | database locked across runs |
| 4 | a delete that printed success and did nothing | **a 401 with nothing to do with auth** |
| 5 | `DefaultAzureCredential` aborting off Azure | an outbox that claimed rows and never published |
| 6 | `source .env` truncating at the first `;` | `azureMonitor=enabled` and zero telemetry |
| 7 | hard kill discarding the batched spans | complete logs, empty workspace |

Bugs 4, 5 and 6 share a shape worth naming: **the symptom pointed somewhere other than the
cause.** A truncated connection string logs "enabled". A credential chain that aborts reports an
outbox problem. A delete that fails reports an authentication failure.

Two more were in the verification itself, which is worse than a bug in the code:

- The drain check **guessed at a response shape**, found no array, fell back to an empty list,
  summed zero and announced "drained" while twelve rows were unpublished. It now reads the
  `pending` field and returns `?` rather than `0` when it cannot tell, so "cannot tell" can never
  be mistaken for "nothing left to do".

- The KQL runner queried the wrong endpoint, and once fixed, rendered nothing — `az monitor
  app-insights query` returns unusable output for `-o table` and a row *count* for `-o tsv`. So
  `roles seen: 1` was the number one, not a role name, and an assertion failed against data that
  had been there all along. Everything now requests `-o json` and formats locally.

A check that cannot fail is not a check, and the way it hides is by having a fallback that looks
like success.
