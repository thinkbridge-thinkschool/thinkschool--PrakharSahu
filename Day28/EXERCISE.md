# Day 28 — Design review + ADR

> **Exercise:** Paste the ADR + your day-by-day build plan + the top critique you got and how it
> changed the design.

| | |
|---|---|
| Capstone under review | `Dispatch` — `Day22/piece2`, 17 projects, 58 tests passing |
| ADR | [`adr/0001-modular-monolith-over-microservices.md`](adr/0001-modular-monolith-over-microservices.md) |
| Full review | [`docs/design-critique.md`](docs/design-critique.md) — 5 findings |
| Build plan | [`docs/build-plan.md`](docs/build-plan.md) — 10 days |

**On the provenance of the critique.** The review was carried out by Claude at my request, and is
labelled that way throughout. It is not mentor or peer feedback. The exercise asks for a critique
that *changed the design*, and a fabricated quote from a mentor would make the ADR dishonest at
its foundation. Every finding below was checked against the code rather than inferred from the
design document.

---

## 1. The top critique

> **"The exit is already built" is not true, and the design's own bug report is the proof.**

`DESIGN.md` rests its central argument on this:

> *The exit is already built. `InProcessIntegrationEventPublisher` is one class in the host. It
> gets replaced with a Service Bus topic and no module changes, because no module was ever allowed
> to know which it was talking to.*

Four lines later it documents a bug whose existence depends entirely on which transport is in use:
the in-process bus makes publishes **re-entrant**, which produced a real defect caught by
`A_double_booked_technician_sends_the_order_back_to_triage`. *"A broker would not have reproduced
it."*

Both cannot be true. If behaviour differs by transport, the transport is not an implementation
detail — it is a load-bearing assumption the module code has been written against.

### The instance nobody had noticed

`ReservationFailedHandler` is the compensating action for the scheduling saga. It calls
`ReturnToTriage`, guarded on `Status == Scheduled`. When the guard refuses, the handler logs at
**Information**:

> *"Expected when the order was already cancelled or the event was redelivered."*

Two cases, and on an in-process bus those genuinely are the only two — the publish is synchronous,
so the reply arrives before any human could act.

**Under a broker there is a third: the order has moved to `InProgress`.** A dispatcher hits
"Start" in the gap between publish and reply. Then:

| | |
|---|---|
| Work order | `InProgress` → `Completed` |
| Scheduling | no reservation exists for that technician |
| Billing | invoices the job |
| Reality | a technician worked a job they were never booked for |
| The only trace | one Information log line reading *"Expected"* |

A silent business-level divergence, currently impossible **only** because of a property of the
transport the design calls swappable.

### How it changed the design

| # | Change | Cost |
|---|---|---|
| 1 | **The claim came out of the ADR.** Consequences now state that swapping the transport is a behavioural change requiring every handler to be re-examined. | The biggest single difference between what I would have written before the review and after. |
| 2 | **The log line became a work item.** A failure arriving when the order is `InProgress` is an anomaly, not something to shrug at. | Small. |
| 3 | **The build plan was reordered.** The outbox and a real broker moved from late "just plumbing" items to **days 3 and 4**, before the API and read models. | Four days of slower visible progress, against re-examining every handler written in the meantime. |

The other four findings — no saga timeout, computed-not-stored SLA breach rewriting historical
reports, the double-booking invariant deferred to a database that does not exist, and Billing
possibly being an integration rather than a context — are in
[`docs/design-critique.md`](docs/design-critique.md).

---

## 2. The ADR

Full text: [`adr/0001-modular-monolith-over-microservices.md`](adr/0001-modular-monolith-over-microservices.md).
The shape of it:

**Context.** Three bounded contexts fell out of one end-to-end slice. The boundaries are real —
"technician" means an id in WorkManagement, the central entity in Scheduling, and nothing at all in
Billing. The open question is how they are deployed and how they communicate.

**Options considered, each argued fairly:**

| Option | Verdict |
|---|---|
| **A — Microservices from day one** | Rejected. Buys enforcement, which Option C also buys for far less, and pays in the currency I have least of: every boundary change becomes a migration, and the boundaries are the thing I am least confident about. |
| **B — Layered monolith, no boundaries** | Rejected on evidence, not principle. `Day27/backend` was this shape; splitting it surfaced three couplings nobody had chosen — a job handler needing the quote repository, authentication buried in `AddInfrastructure`, one logging category for the whole application. |
| **C — Modular monolith, enforced** | **Chosen.** |

**The decision, in one line:** *buying the ability to be wrong about the boundaries cheaply, and
paying for it with a transport that does not behave like the one I will eventually use.*

**Consequences** — good (a boundary move is a refactor; enforcement is demonstrated by a failing
build; the exit is one class) and bad (the transport is not swappable for free; a crash between
commit and publish loses the event; a failed handler drops it; no saga timeout).

**What would make me revisit it** — four falsifiable triggers, so the decision can be shown wrong
rather than merely disagreed with: independent scaling need, deploys blocking between teams, one
module degrading another in production, or a boundary surviving six months unmoved.

---

## 3. The build plan

Full detail: [`docs/build-plan.md`](docs/build-plan.md). Ten days, assumed; the tail compresses if
there are fewer. Days 1–4 are not negotiable.

| Day | What | Exit condition |
|---|---|---|
| 1 | Persistence — EF Core, **a schema per module**, not a shared context | The 58 existing tests pass unchanged against a real database |
| 2 | The double-booking constraint in Scheduling | Two concurrent overlapping reservations; exactly one survives, the loser produces a reservation-failed event |
| 3 | Transactional outbox into `WorkOrderService.PublishAsync` | The Day 20 crash proof, re-run against Dispatch |
| 4 | **Swap to Service Bus, then re-examine every handler** | A failure event delivered *after* the order reaches `InProgress` is treated as an anomaly — a test that fails today |
| 5 | Saga timeout | Scheduling never replies; the order returns to `Triaged` within the deadline |
| 6 | The API surface, versioned from the first commit | Full lifecycle over HTTP, ending in a drafted invoice |
| 7 | Read models — "today's work across every technician" | One endpoint answering it with the architecture tests still passing |
| 8 | Entra ID, deny-by-default, domain roles | A test per role for what it may and may not do |
| 9 | Deploy into the infrastructure Days 23–24 already provisioned | The day-6 lifecycle test, run against the deployed environment |
| 10 | OpenTelemetry, then a STRIDE-lite security pass | One trace spanning the lifecycle across process boundaries |

**Why the ordering is unusual.** The obvious plan puts infrastructure last. The review killed that:
every handler written against the in-process bus inherits an assumption that will not survive a
broker, so the real transport lands on day 4, before the API and before any new handler exists.

**Where the plan could be wrong.** Day 4 is the bet. If re-examining the handlers takes two days
rather than one, days 9 and 10 compress into one. Better to discover that on day 4 with five
handlers than on day 9 with twenty.
