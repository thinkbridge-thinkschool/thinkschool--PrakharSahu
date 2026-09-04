# Day 22 piece 2 — Capstone kickoff: design + scaffold

> **Exercise:** Paste the repo URL + the one-page design (contexts, aggregate, async flows) and
> the scaffolded solution layout.

---

## Repo

```
https://github.com/Avi-0009/Prakhar_Sahu        Day22/piece2/
https://github.com/thinkbridge-thinkschool/thinkschool---Prakhar-Sahu   Day22/piece2/
```

Branch: `feature/day22-piece2`. Everything below is in `Day22/piece2/`.

---

## The one-page design

Full version: **[DESIGN.md](DESIGN.md)**. Condensed here.

### The product slice

A field-service platform. A customer reports a fault → someone triages it → a technician is
booked → the work is done → the customer is invoiced.

One slice followed all the way through, rather than a thin layer across many features. The
questions worth answering at kickoff — where do the boundaries go, what must be transactionally
consistent, what can be eventually consistent — only surface when one path is followed end to end.

### Bounded contexts

| Context | Owns the question | Core aggregate |
|---|---|---|
| **WorkManagement** *(core)* | What needs doing, and how far along is it? | `WorkOrder` |
| **Scheduling** *(supporting)* | Who is free, and when? | `Reservation` |
| **Billing** *(supporting)* | What does it cost, and who pays? | `Invoice` |

**The word that proves the boundaries are real: "technician".**

- In **WorkManagement** it is a `TechnicianId` and nothing else — all a work order needs is who to
  attribute labour to.
- In **Scheduling** it has a calendar, a shift and a skill set. It is what the module is built
  around.
- In **Billing** it does not exist. An invoice is priced from minutes and a rate; who did the work
  is not an accounting concern.

Three models of one word, none of them wrong. The alternative — one shared `Technician` class
carrying every field any context ever needed — satisfies none of them and cannot be changed by any
of them without consulting the other two. That shared class is the most common way a "modular"
system turns out not to be.

### The core aggregate

```
Raised ──triage──▶ Triaged ──schedule──▶ Scheduled ──start──▶ InProgress ──complete──▶ Completed
                      ▲                       │
                      └────returnToTriage─────┘        (Scheduling refused the booking)

   any state except Completed ──cancel──▶ Cancelled
```

**Inside the boundary:** the order's state, its scheduled window, its labour entries.
**Outside, by id:** technician, customer, invoice.

An aggregate is a **transactional consistency boundary**, not a convenience grouping. The test for
membership is whether an invariant requires it to be consistent *at the moment of commit*.

- Labour entries are **in**, because *"cannot complete with no labour logged"* must be answerable
  without a query. Move them out and the check becomes a race.
- Technician is **out**. No invariant of a work order depends on a technician's internal state.
  Pulling them in would mean loading a technician to save a work order, locking that row against
  every other order being saved at that moment — a busy technician would become the system's
  hottest write lock, protecting a rule that is not even about them.
- Invoice is **out**. A work order that could not be completed while the accounting system was
  down would couple a field engineer's day to a ledger.

Every mutation returns `Result` rather than throwing — a dispatcher clicking "start" on an order
somebody else just cancelled is normal traffic from a stale UI, not an exceptional condition.
Every property has a private setter, so the only route into a new state is a method named after
something that happens in the business.

### Async flows

**1. Scheduling — a saga with a compensating action**

```
WorkManagement                    Scheduling
──────────────                    ──────────
Schedule()  ──── WorkOrderScheduledV1 ────▶  is the technician free?
  status = Scheduled                              ├── yes ──▶ TechnicianReservedV1
  (committed)                                     └── no ───▶ TechnicianReservationFailedV1
  ReturnToTriage()  ◀───────────────────────────────────────────┘
```

WorkManagement asserts *intent*, not fact — it cannot see a calendar and never should. Two modules
cannot share a transaction, so the price of the boundary is a compensating action rather than a
distributed transaction.

**2. Billing — decoupling the critical path**

```
WorkManagement ──── WorkOrderCompletedV1 ────▶ Billing: draft an invoice
```

A field engineer taps "done" on a phone with two bars of signal. If completion required Billing to
price and store an invoice in the same request, an accounting problem would stop engineers
finishing work. Everything Billing needs travels **on the event**, so it never calls back — a
synchronous query across a module boundary is a synchronous coupling wearing an asynchronous
costume.

**3. SLA sweeper — reacting to the absence of an event**

A `BackgroundService` on a timer. The other flows react to something someone caused; this reacts to
*nothing happening*, which is the whole problem with deadlines. Breach is computed, never stored.

---

## The scaffolded solution layout

```
Dispatch.slnx                                   16 projects
├── src/
│   ├── Dispatch.Api/                           ← single deployable; composition root
│   │   ├── Messaging/InProcessIntegrationEventPublisher.cs
│   │   └── Endpoints/WorkOrderEndpoints.cs
│   ├── Dispatch.SharedKernel/                  ← Entity, AggregateRoot, Result, events, IClock
│   │   ├── Entity.cs   Result.cs   Events.cs   Messaging.cs
│   └── Modules/
│       ├── WorkManagement/
│       │   ├── Dispatch.WorkManagement.Contracts/        ← the ONLY door in
│       │   │   └── WorkManagementEvents.cs
│       │   ├── Dispatch.WorkManagement.Domain/           ← the aggregate + invariants
│       │   │   └── WorkOrders/{WorkOrder,ValueObjects,Identifiers,Events,Errors}.cs
│       │   ├── Dispatch.WorkManagement.Application/      ← use cases + ports
│       │   │   ├── Abstractions/Ports.cs
│       │   │   └── WorkOrders/{WorkOrderService,ReservationFailedHandler}.cs
│       │   └── Dispatch.WorkManagement.Infrastructure/   ← adapters + registration
│       │       ├── Persistence/InMemoryWorkOrderStore.cs
│       │       ├── SlaSweeper.cs
│       │       └── WorkManagementModule.cs
│       ├── Scheduling/       (identical four layers)
│       └── Billing/          (identical four layers)
└── tests/
    ├── Dispatch.ArchitectureTests/              12 rules
    ├── Dispatch.WorkManagement.Domain.Tests/    35 invariants
    └── Dispatch.WorkManagement.Application.Tests/ 11 cross-module flows
```

### The reference graph

Dependencies point inwards: `Infrastructure → Application → Domain → SharedKernel`. Ports are
declared in Application and implemented in Infrastructure.

**The only three cross-module edges in the entire solution:**

```
Dispatch.Scheduling.Application     → Dispatch.WorkManagement.Contracts
Dispatch.Billing.Application        → Dispatch.WorkManagement.Contracts
Dispatch.WorkManagement.Application → Dispatch.Scheduling.Contracts
```

Every one lands on `*.Contracts`. None reaches a `Domain`, `Application` or `Infrastructure`.

Full generated graph: [`docs/reference-graph.txt`](docs/reference-graph.txt).

---

## The part that is not just a folder layout

**Twelve architecture tests fail the build on a violation.**

| Rule | What it stops |
|---|---|
| `Domain_depends_on_nothing_but_the_shared_kernel` | a domain model that needs a database to be tested |
| `Application_never_references_infrastructure` | the inverted arrow that makes the layers decorative |
| `Contracts_depend_on_nothing_but_the_shared_kernel` | one module's packages becoming everyone's |
| `No_module_reaches_into_another_modules_internals` | two modules wearing two folder names |
| `Only_the_host_composes_infrastructure` | a module reaching for another module's database |
| `The_shared_kernel_depends_on_nothing` | a shared kernel that grows dependencies |
| `Every_module_has_the_same_four_layers` | a module that put something in the wrong one |
| `The_documented_cross_module_edges_are_the_only_ones` | a coupling nobody discussed |
| `No_domain_assembly_knows_about_a_database_a_web_framework_or_a_broker` | transitive infrastructure |
| `Contracts_expose_primitives_only` | a published event that freezes the internal model |
| `Domain_events_never_leak_into_a_published_contract` | internal events becoming public API |
| `Aggregate_roots_have_no_public_setters` | invariants downgraded to suggestions |

**Proven, not asserted.** [`docs/architecture-guardrail-proof.txt`](docs/architecture-guardrail-proof.txt)
adds `WorkManagement.Domain → Scheduling.Domain` on purpose and shows three rules catching it:

```
$ dotnet add Dispatch.WorkManagement.Domain reference Dispatch.Scheduling.Domain

Dispatch.WorkManagement.Domain must reference only Dispatch.SharedKernel,
  but also references: Dispatch.Scheduling.Domain
Cross-module references must target *.Contracts only. Found:
  Dispatch.WorkManagement.Domain -> Dispatch.Scheduling.Domain

Failed!  - Failed: 3, Passed: 9, Total: 12
```

Nobody adds a forbidden reference on purpose. They add it at 5pm because the type they needed
happened to be over there, and by the time anyone notices there are forty of them.

---

## Verification

```
tests/Dispatch.ArchitectureTests                 12 passed
tests/Dispatch.WorkManagement.Domain.Tests       35 passed
tests/Dispatch.WorkManagement.Application.Tests  11 passed
scripts/smoke.sh (real HTTP, running host)        8 passed, 0 failed
```

The smoke test drives a work order through the whole system over HTTP: raised → refused
out-of-order transitions → triaged → scheduled → a clashing booking compensated back to triage →
cancelled → the freed slot rebooked by another order.

### A real bug the tests found

`A_double_booked_technician_sends_the_order_back_to_triage` failed with *"Collection was
modified"*.

The in-process bus makes publishing **synchronous, and therefore re-entrant**:

```
Schedule() ─▶ publish WorkOrderScheduledV1
           ─▶ Scheduling sees a clash, publishes TechnicianReservationFailedV1
           ─▶ WorkManagement's handler calls ReturnToTriageAsync on THIS SAME aggregate
           ─▶ the aggregate raises another domain event
           ─▶ ...into the list the publish loop is still iterating
```

Fixed by snapshotting and clearing domain events *before* dispatching them. A broker would not
have reproduced it — the compensating event would arrive in a later request on a freshly loaded
aggregate — which is exactly why it is worth knowing now rather than the week the transport
changes.

A second defect surfaced over HTTP: `{"priority":"High"}` returned 400 because
`System.Text.Json` binds enums numerically by default, making the API's contract a set of magic
numbers. Fixed with `JsonStringEnumConverter`.

---

## Why a modular monolith and not microservices

The boundaries are real — versioned contracts, no access to internals, a build that fails if that
changes. What is *not* real is the network between them: delivery is a method call.

- **Boundaries drawn in week one are usually wrong.** Moving one here is a refactor. Moving one
  between deployed services is a migration with a compatibility window.
- **Every distributed-systems problem is optional right now.** No broker, no partition, no
  serialisation format to agree, no distributed trace needed to answer "why did nothing happen".
- **The exit is already built.** `InProcessIntegrationEventPublisher` is one class in the host.
  Replace it with a Service Bus topic and no module changes, because none of them was ever allowed
  to know which it was.

---

## Known gaps, deliberately

1. **No persistence** — all three stores are dictionaries. Picking a database before the aggregate
   boundaries have met a real requirement means every schema decision is a migration to undo.
2. **Publish-after-commit is two operations** — a crash between them loses the event. Day 20's
   outbox slots into `WorkOrderService.PublishAsync` without any other file changing.
3. **A failed handler drops its event** — no retry, no dead-letter.
4. **The overlap check races** — two concurrent bookings can both pass it. The fix is a database
   constraint, not more C#.
5. **No auth, no read models, no UI** — out of scope for a design-and-scaffold day.
