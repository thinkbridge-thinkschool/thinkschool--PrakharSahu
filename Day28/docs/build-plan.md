# Dispatch — day-by-day build plan

**Where this starts.** `Dispatch` is designed, scaffolded and tested: 17 projects, 58 tests
passing, boundaries enforced. Every store is a dictionary. There is no database, no API, no
authentication and no UI. The infrastructure from Days 23–24 was provisioned under the name
`dispatch` but the application has never been deployed into it, and Days 25–27 — managed identity,
tracing, the security pass — were applied to the Quotes API instead.

**Assumption, stated because it drives the shape.** Ten build days. If there are fewer, the tail
compresses: days 9 and 10 are the first to go, then 8. Days 1–4 are not negotiable, for the reason
in the ordering note below.

**One rule for every day.** It ends with something demonstrable and something asserted. No day is
"finish X" — each has an exit condition a test can check.

---

## The ordering decision

The obvious plan is: persistence, API, UI, then infrastructure last because it is "just plumbing".

**The design review killed that.** `ReservationFailedHandler` is correct today only because the
in-process bus delivers synchronously, and the same is true of the re-entrancy fix in
`WorkOrderService`. Every handler written against that bus inherits an assumption that will not
survive a broker.

So the real transport comes in on **day 4**, before the API and before any new handler exists. The
cost of moving it later is re-examining every handler written in between; the cost of moving it
now is four days of slightly slower progress.

---

## Week one — make it real

### Day 1 — Persistence, one context at a time

Replace the dictionaries with EF Core against SQL. **A schema per module**, not a shared
`DbContext` — this is the mistake I made in `Day27/backend`, where one shared context means every
module's Infrastructure transitively sees every module's entities.

- `WorkManagement` first, since `WorkOrder` has the only interesting mapping: the labour
  collection is owned, value objects are `ComplexProperty`, and `Status` is a converted enum.
- Then `Scheduling` and `Billing`.

**Exit:** the 58 existing tests pass unchanged against a real database. The domain tests still run
with a `new` and a fake clock — if persistence has leaked into the domain, they will tell me.

### Day 2 — The double-booking constraint

Known gap 4. The overlap check races: two concurrent bookings both pass it and both insert.

- A unique index or exclusion constraint on `(TechnicianId, window)` in Scheduling.
- The application catches the constraint violation and turns it into
  `TechnicianReservationFailedV1` — the failure path that already exists.

**Exit:** a test that fires two overlapping reservations concurrently and asserts exactly one
survives, with the loser producing a reservation-failed event rather than an exception.

This is day 2 rather than later because it is the one rule Scheduling exists to enforce, and
because it is the cleanest demonstration that the compensating saga works under real contention.

### Day 3 — The transactional outbox

Known gap 2. Persist-then-publish is two operations; a crash between them loses the event
silently.

- Port the Day 20 outbox into `WorkOrderService.PublishAsync`.
- Outbox row and aggregate change commit in one transaction.
- A relay that claims, publishes, then marks — in that order.

**Exit:** the Day 20 crash proof, re-run against Dispatch. Kill the process between commit and
publish; the event is still delivered after restart.

### Day 4 — Swap the transport, and re-examine every handler

The day the review made necessary.

- `InProcessIntegrationEventPublisher` is replaced by Service Bus behind the same port.
- **Then the actual work:** go through every handler and ask what it assumed about delivery.
  Known starting points — `ReservationFailedHandler`'s "Expected" log line, and the re-entrancy
  snapshot in `WorkOrderService` which may now be unnecessary.
- Handlers become idempotent against real at-least-once delivery, not against a method call that
  happens not to repeat.

**Exit:** a test that delivers `TechnicianReservationFailedV1` *after* the order has moved to
`InProgress`, and asserts the system treats it as an anomaly — not a log line saying "Expected".
That test fails today and is the review's finding made executable.

### Day 5 — Saga timeout

The review's finding 1. Nothing watches an order stuck in `Scheduled` awaiting a reply that never
comes.

- A reservation deadline recorded when `WorkOrderScheduledV1` is published.
- A sweeper that finds orders past it and triggers the same compensation as an explicit failure.

**Exit:** a test where Scheduling never replies and the order returns to `Triaged` within the
deadline rather than sitting forever.

---

## Week two — make it usable, then make it safe

### Day 6 — The API surface

Minimal APIs over the existing use cases. Each module maps its own endpoints; the host composes
them, exactly as `Day27/backend` does after its split.

- Versioned from the first commit — `/api/v1`. Day 27's lesson: retrofitting a version breaks
  every caller at once.
- One endpoint per domain operation, named after the operation rather than the HTTP verb.

**Exit:** the full lifecycle driven over HTTP — raise, triage, schedule, start, log labour,
complete — with an invoice drafted at the end.

### Day 7 — Read models

The thing a dispatcher actually needs: *today's work across every technician*. That query spans all
three contexts, and with strict boundaries it has nowhere to live.

- A read model fed by the integration events already published.
- It belongs to neither module. It is a separate projection with its own store.

**Exit:** one endpoint answering the dispatcher's question, built without any module referencing
another's internals — the architecture tests still pass.

This is the day the modular monolith either holds or shows its first real crack, which is why it
gets a whole day rather than being folded into day 6.

### Day 8 — Authentication and authorization

- Entra ID, matching Day 25.
- Deny-by-default authorization from the first line — Day 27's single most valuable finding was
  that opt-in authorization means one forgotten attribute is a public endpoint.
- Roles that match the domain: dispatcher, technician, finance. A technician may log labour only
  against an order assigned to them.

**Exit:** a test per role asserting both what it may do and what it may not.

### Day 9 — Deploy it

Days 23 and 24 provisioned infrastructure named `dispatch` and never put anything in it.

- Container image, azd, deployment stack.
- Managed identity to SQL and Service Bus. No connection strings anywhere — Day 25 proved this is
  achievable rather than aspirational.

**Exit:** the day-6 lifecycle test, run against the deployed environment.

### Day 10 — Observability, then a security pass

- OpenTelemetry, App Insights, a distributed trace stitching API → outbox → worker → database.
  With a real broker on day 4 this is a genuine distributed trace rather than one process drawing
  itself.
- STRIDE-lite over the result, the private-endpoint posture from Day 27, a ZAP baseline.

**Exit:** one trace spanning the full lifecycle across process boundaries, and a hardening
verification script in the shape of Day 27's seventeen assertions.

---

## What this plan deliberately leaves out

- **A UI.** The API and read models are the deliverable. A UI would consume days and demonstrate
  nothing about the architecture.
- **Splitting Billing out to a real accounting integration.** The review raised it (finding 4) and
  it is the right question, but it is a design decision needing a real requirement first.
- **Storing SLA breach at the moment of breach.** Review finding 2. One day's work, and it matters
  only once somebody asks for a historical report.

## How this plan could be wrong

Day 4 is the bet. If re-examining the handlers turns out to be a two-day job rather than a one-day
job, the tail slips and days 9 and 10 compress into one. I would rather discover that on day 4 with
five handlers than on day 9 with twenty.
