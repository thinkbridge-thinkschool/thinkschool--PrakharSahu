# Dispatch — one-page design

**The product slice.** A field-service platform: a customer reports a fault, someone triages it,
a technician is booked, the work is done, and the customer is invoiced. One slice, end to end,
rather than a broad thin layer across many features — because the questions worth answering at
kickoff (where do the boundaries go, what must be transactionally consistent, what can be
eventually consistent) only show up when one path is followed all the way through.

**Shape.** A modular monolith. Three modules, one deployable, one process. Clean architecture
inside each module. Not microservices — see the last section for why not.

---

## Bounded contexts

Three, each owning a different question and answering to a different part of the business.

| Context | Owns the question | Core aggregate | Talks to |
|---|---|---|---|
| **WorkManagement** *(core)* | *What needs doing, and how far along is it?* | `WorkOrder` | publishes to both others |
| **Scheduling** *(supporting)* | *Who is free, and when?* | `Reservation` | reacts to WorkManagement |
| **Billing** *(supporting)* | *What does it cost, and who pays?* | `Invoice` | reacts to WorkManagement |

WorkManagement is the core domain: it is the thing this product is *for*, and the place where
getting the model right is worth the most effort. The other two are supporting — necessary,
valuable, but the business would not choose to build them if it could buy them.

### The word that proves the boundaries are real

**"Technician" means something different in every context, and that is correct.**

- In **WorkManagement** a technician is a `TechnicianId` and nothing more. All a work order needs
  to know is who to attribute the labour to.
- In **Scheduling** a technician has a calendar, a shift and a skill set. It is the entity the
  whole module is organised around.
- In **Billing** a technician does not exist at all. An invoice is priced from minutes and a
  rate; who did the work is not an accounting concern.

The alternative — one shared `Technician` class carrying every field any context ever needed —
satisfies none of them and cannot be changed by any of them without consulting the other two.
That single shared class is the most common way a "modular" system turns out not to be.

The same is true of "customer": an id in WorkManagement, an id in Billing, and the actual
customer record lives in a CRM context this system does not own yet.

---

## The core aggregate: `WorkOrder`

```
Raised ──triage──▶ Triaged ──schedule──▶ Scheduled ──start──▶ InProgress ──complete──▶ Completed
                      ▲                       │
                      └────returnToTriage─────┘        (Scheduling refused the booking)

   any state except Completed ──cancel──▶ Cancelled
```

**Inside the boundary** — the order's own state, its scheduled window, and its labour entries.

**Outside, referenced by id** — technician, customer, invoice.

An aggregate is a *transactional consistency boundary*, not a convenience grouping. The test for
membership is: **does an invariant require this to be consistent at the moment of commit?**

- Labour entries are **in**, because *"a work order cannot be completed with no labour logged"*
  has to be answerable without a query. Move them out and the check becomes a race.
- The technician is **out**, because no invariant of a work order depends on the technician's
  internal state. Pulling them in would mean loading a technician to save a work order, locking
  that row against every other order being saved at the same moment — a busy technician would
  become the system's hottest write lock, protecting a rule that is not even about them.
- The invoice is **out**, because a work order that could not be completed while the accounting
  system was down would be an absurd coupling of a field engineer's day to a ledger.

Every extra entity pulled inside is another row locked in the same transaction. Aggregates are
almost always smaller than they are first drawn.

### The invariants it enforces

| Rule | Why it is a rule |
|---|---|
| Cannot schedule an untriaged order | dispatch order would be arrival order, not urgency order |
| A window cannot start in the past, and must end after it starts | always a clock, timezone or stale-form bug |
| Cannot start before the window opens | protects the arrival data the SLA report depends on |
| Labour only while `InProgress` | a completed order's total is already invoiced |
| **Cannot complete with no labour logged** | the rule the boundary was drawn around |
| Cannot cancel a completed order | the completion event has already left the module |
| Cancelling requires a reason | "why did this not happen" is the first question asked |
| SLA due date is derived at triage, once | recalculating on read would move deadlines already promised |

Every mutation returns `Result`, never throws. A dispatcher clicking "start" on an order somebody
else just cancelled is normal traffic from a stale UI, not an exceptional condition. Exceptions
are reserved for genuine bugs.

Every property has a private setter. The only way to change a work order is to call a method
named after something that happens in the business — which is what makes the invariants
enforceable, because there is no path to a bad state that skips the rules.

---

## Async flows

Three, and each exists for a different reason.

### 1. Scheduling — a saga with a compensating action

```
WorkManagement                    Scheduling
──────────────                    ──────────
Schedule()  ──── WorkOrderScheduledV1 ────▶  is the technician free?
  status = Scheduled                              │
  (committed)                                     ├── yes ──▶ hold the slot
                                                  │           TechnicianReservedV1
                                                  │
                                                  └── no ───▶ TechnicianReservationFailedV1
  ReturnToTriage()  ◀───────────────────────────────────────────┘
  status = Triaged
```

WorkManagement asserts *intent*, not fact — it has no visibility of anyone's calendar and never
should have. Scheduling is the only module that can answer "is this person free", and it answers
after the work order has already committed.

**Two modules cannot share a transaction**, so the price of that is a compensating action. The
alternative is a distributed transaction, which is the coupling this whole structure exists to
avoid. Returning to triage is a first-class domain operation, not a quiet field reset, because
"we told you we were coming and now we are not" is a real business event.

### 2. Billing — decoupling the critical path

```
WorkManagement ──── WorkOrderCompletedV1 ────▶ Billing: draft an invoice
                    (id, customer, minutes,          InvoiceDraftedV1
                     withinSla, completedAt)
```

A field engineer taps "done" on a phone with two bars of signal. If completing the job required
Billing to price and store an invoice in the same request, an accounting problem would stop
engineers finishing work. Instead the order commits, the event goes out, and this happens
whenever it happens.

Everything Billing needs travels **on the event** — so it never calls back into WorkManagement. A
synchronous query across a module boundary is a synchronous coupling wearing an asynchronous
costume.

The pricing rule (rate, rounding) lives in Billing and nowhere else. WorkManagement reports
minutes and has no idea what they cost.

### 3. SLA sweeper — reacting to the absence of an event

A `BackgroundService` on a one-minute timer, looking for open orders past their due date.

The other two flows react to something someone caused. This one reacts to *nothing happening*,
which is the whole problem with deadlines — so it needs a clock and a loop rather than a
subscription.

Breach is **computed, never stored**. A stored flag is wrong from the moment the deadline passes
until the next sweep, and writing to every open order to keep a boolean honest is a lot of
contention to buy a value a subtraction already gives you.

### Rules that hold across all three

- **Persist first, publish second.** Publishing before the commit announces a decision the
  transaction may still roll back, and no subscriber cleverness can un-send the email.
- **Every handler is idempotent.** Delivery is at-least-once whether the transport is a list or a
  broker. Each handler dedupes on its own prior work — Billing looks for an existing invoice,
  Scheduling looks for an existing reservation.
- **Handlers never throw to signal a business decision.** "I chose not to act" is a log line; an
  exception tells the transport to redeliver, turning a decision into an infinite loop.
- **Contracts carry primitives only, and are versioned in the name.** A published event with a
  domain type in it drags the internal model across the boundary and freezes it.

---

## The scaffolded solution

```
Dispatch.slnx
├── src/
│   ├── Dispatch.Api/                              ← the single deployable; composition root
│   │   ├── Messaging/InProcessIntegrationEventPublisher.cs
│   │   └── Endpoints/
│   ├── Dispatch.SharedKernel/                     ← Entity, AggregateRoot, Result, events, IClock
│   └── Modules/
│       ├── WorkManagement/
│       │   ├── Dispatch.WorkManagement.Contracts/       ← the ONLY door in
│       │   ├── Dispatch.WorkManagement.Domain/          ← WorkOrder + invariants
│       │   ├── Dispatch.WorkManagement.Application/     ← use cases + ports
│       │   └── Dispatch.WorkManagement.Infrastructure/  ← adapters + module registration
│       ├── Scheduling/       (same four)
│       └── Billing/          (same four)
└── tests/
    ├── Dispatch.ArchitectureTests/                ← the rules, enforced
    ├── Dispatch.WorkManagement.Domain.Tests/
    └── Dispatch.WorkManagement.Application.Tests/
```

**Dependencies point inwards.** Infrastructure → Application → Domain → SharedKernel. Ports are
declared in Application and implemented in Infrastructure; invert that one arrow and the layers
keep their names and lose their value.

**The only three cross-module edges in the entire solution:**

```
Dispatch.Scheduling.Application     → Dispatch.WorkManagement.Contracts
Dispatch.Billing.Application        → Dispatch.WorkManagement.Contracts
Dispatch.WorkManagement.Application → Dispatch.Scheduling.Contracts
```

All three land on `*.Contracts`. None reaches a `Domain`, `Application` or `Infrastructure`.

**And they are enforced, not documented.** `Dispatch.ArchitectureTests` fails the build on a
forbidden reference — proof in
[`docs/architecture-guardrail-proof.txt`](docs/architecture-guardrail-proof.txt), where a
deliberately added `WorkManagement.Domain → Scheduling.Domain` reference is caught by three
separate rules. Nobody adds a forbidden reference on purpose; they add it at 5pm because the type
they needed happened to be over there, and by the time anyone notices there are forty of them.

---

## Why a modular monolith and not microservices

The boundaries here are **real**: versioned contracts, no access to internals, a build that fails
if that changes. What is *not* real is the network between them — delivery is a method call.

That is the entire trade, and it is the right one at kickoff:

- **Boundaries drawn in week one are usually wrong.** Moving one here is a refactor. Moving one
  between deployed services is a migration with a compatibility window.
- **Every distributed-systems problem is optional right now.** No broker to run, no partition to
  survive, no serialisation format to agree, no distributed trace needed to answer "why did
  nothing happen".
- **The exit is already built.** `InProcessIntegrationEventPublisher` is one class in the host. It
  gets replaced with a Service Bus topic and no module changes, because no module was ever
  allowed to know which it was talking to.

The cost is honest and worth stating: an in-process bus makes publishes synchronous and therefore
**re-entrant** — a scheduling failure comes back into the same aggregate instance mid-publish.
That produced a real bug in this scaffold, caught by
`A_double_booked_technician_sends_the_order_back_to_triage`, and fixed by snapshotting domain
events before dispatching them. A broker would not have reproduced it, which is exactly why it is
worth knowing about now rather than the week the transport changes.

---

## Known gaps, deliberately

Stated so they are decisions rather than oversights.

1. **No persistence.** All three stores are dictionaries. Picking a database on day one means
   picking it before the aggregate boundaries have met a single real requirement. The ports exist
   so the choice is an Infrastructure change and nothing else.
2. **Publish-after-commit is two operations.** A crash between them loses the event silently. The
   transactional outbox from Day 20 is the fix and slots into `WorkOrderService.PublishAsync`
   without any other file changing.
3. **A failed handler drops its event.** No retry, no dead-letter. A broker gives both; the
   in-process bus does not.
4. **The overlap check races.** Two concurrent bookings can both pass it and both insert. The fix
   is a database constraint, not more C#.
5. **No authentication, no read models, no UI.** All out of scope for a design-and-scaffold day.
