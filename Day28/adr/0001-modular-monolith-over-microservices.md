# ADR-0001 — A modular monolith, not microservices

- **Status:** Accepted
- **Date:** 2026-09-14
- **Deciders:** me, informed by the design review in [`docs/design-critique.md`](../docs/design-critique.md)
- **Supersedes:** nothing
- **Applies to:** `Dispatch` — the field-service capstone (`Day22/piece2`)

---

## Context

Dispatch models one slice of a field-service business end to end: a customer reports a fault,
someone triages it, a technician is booked, the work is done, the customer is invoiced.

Three bounded contexts fell out of that slice, each answering a different question and each
answering to a different part of the business:

| Context | Owns the question | Aggregate |
|---|---|---|
| **WorkManagement** *(core)* | What needs doing, and how far along is it? | `WorkOrder` |
| **Scheduling** *(supporting)* | Who is free, and when? | `Reservation` |
| **Billing** *(supporting)* | What does it cost, and who pays? | `Invoice` |

The boundaries are real, and there is a concrete test for it: the word **"technician"** means
something different in each. In WorkManagement it is a `TechnicianId` and nothing more. In
Scheduling it is the entity the whole module is organised around — calendar, shift, skill set. In
Billing it does not exist at all; an invoice is priced from minutes and a rate.

Given three contexts with genuinely separate models, the open question is how they are
**deployed** and how they **communicate**. That is this decision.

It has to be made now, at kickoff, because it determines the project structure, the test strategy,
the local development story, and what it costs to move a boundary later.

## Decision drivers

1. **The boundaries drawn in week one are probably wrong.** This is a first pass at a domain I do
   not understand well yet. Whatever makes redrawing them cheap wins.
2. **The team is one person.** Anything requiring per-service pipelines, dashboards and on-call is
   a cost with no matching benefit.
3. **The programme has already covered the distributed-systems failure modes** — background jobs,
   messaging, the transactional outbox, caching, resilience. The question is whether to *pay* for
   them here, not whether they are understood.
4. **The boundaries must be enforceable, not aspirational.** A "modular" system whose modules
   quietly reach into each other is a big ball of mud with extra folders.
5. **Nothing in the workload demands independent scaling.** No component has a load profile
   materially different from the others.

## Considered options

### Option A — Microservices from day one

Three deployables, three databases, a broker between them.

**For it.** Boundaries are enforced by the network: you cannot accidentally reference another
service's internals because you cannot see them. Independent deploy and scale. It is where the
system would end up if it succeeded and grew.

**Against it.** Every boundary change becomes a migration with a compatibility window instead of a
refactor. Every local run needs three processes and a broker. Every cross-context question needs
distributed tracing to answer. And the boundaries are the thing I am least confident about, so
this option makes the most expensive thing to change the thing most likely to be wrong.

**Rejected.** It buys enforcement, which Option C also buys at a fraction of the price, and pays
for it in the currency I have least of.

### Option B — A layered monolith with no module boundaries

One project, folders by technical layer, one shared model.

**For it.** The simplest possible thing. No ceremony, no indirection, fastest to write.

**Against it.** With one shared model, "technician" has to become a single class carrying every
field any context ever needed — satisfying none of them, and unchangeable by any of them without
consulting the other two. That shared class is the most common way a system turns out not to be
modular. There would also be no seam to extract along later: migrating to anything else starts
with an archaeology project.

**Rejected, with direct evidence rather than on principle.** `Day27/backend` was exactly this
shape, and splitting it into modules surfaced three couplings nobody had chosen: a background-job
handler that needed the quote repository, the entire authentication setup buried inside a method
named `AddInfrastructure`, and every endpoint logging under a single category so no log filter
could isolate one feature. None of those were decisions. They were things one project made
possible and nothing made visible.

### Option C — A modular monolith with enforced boundaries *(chosen)*

One deployable, one process. Three modules, each with `Contracts / Domain / Application /
Infrastructure`. A module may reference another module's **Contracts** and nothing else. Modules
communicate by publishing integration events carrying primitives only.

**For it.** The boundaries are real — versioned contracts, no access to internals — while moving
one is a refactor rather than a migration. Every distributed-systems problem is optional: no
broker to run, no partition to survive, no serialisation format to agree. One process to start,
one log to read, one debugger to attach.

**Against it.** The network between the modules is not real. Delivery is a method call, so it is
synchronous, ordered, never lost and never duplicated — none of which will be true later. The
discipline is enforced by a test rather than by physics, so it holds exactly as long as the test
does.

## Decision

**Option C.** One deployable, three modules, boundaries enforced by
`Dispatch.ArchitectureTests` — twelve tests over the project graph and the emitted assemblies,
which fail the build on a forbidden reference.

The whole trade in one line: **I am buying the ability to be wrong about the boundaries cheaply,
and paying for it with a transport that does not behave like the one I will eventually use.**

## Consequences

### Good

- **Moving a boundary is a refactor.** The three modules became the right three only after the
  `WorkOrder` invariants met real requirements. Had they been services, that discovery would have
  cost a migration with a compatibility window.
- **The enforcement is real and demonstrated.** A deliberately added
  `WorkManagement.Domain -> Scheduling.Domain` reference is caught by three separate rules, and
  the failing build is captured in `docs/architecture-guardrail-proof.txt`.
- **The exit is one class.** `InProcessIntegrationEventPublisher` is a single file in the host,
  behind a port no module can see past.

### Bad — and worse than the design originally claimed

`DESIGN.md` said the publisher could be swapped for a Service Bus topic "and no module changes,
because no module was ever allowed to know which it was talking to". **The review showed that is
false**, and the evidence was already in the codebase.

**First, the direction already known.** An in-process publish is synchronous and therefore
**re-entrant**: a scheduling failure comes back into the same aggregate instance mid-publish. That
produced a real bug, caught by `A_double_booked_technician_sends_the_order_back_to_triage` and
fixed by snapshotting domain events before dispatching them. A broker would not have reproduced
it.

**Second, the direction not known, which is worse.** `ReservationFailedHandler` calls
`ReturnToTriage`, which is guarded on `Status == Scheduled`. When that guard rejects the
transition, the handler logs at **Information**:

> *"Expected when the order was already cancelled or the event was redelivered."*

Those are the only two cases the comment considers, and on an in-process bus they genuinely are
the only two — the reply arrives before anybody can act. **Under a broker there is a third: the
order has moved to `InProgress`.** A dispatcher hits "Start" in the gap between the publish and
the reply. Then the compensation silently does nothing, a technician works a job they were never
reserved for, the order completes, Billing invoices it, and the only trace is a log line that says
*Expected*.

So the honest statement of the cost is: **the transport is not an implementation detail.** At
least two handlers are correct today only because of properties the in-process bus happens to
provide. Swapping it is a behavioural change requiring every handler to be re-examined, not a
configuration change.

### Also accepted, with reasons

| | |
|---|---|
| **A crash between commit and publish loses the event silently.** | Persist-then-publish is two operations. The transactional outbox from Day 20 is the fix and slots into `WorkOrderService.PublishAsync` without any other file changing. Scheduled for build day 3. |
| **A failed handler drops its event.** | No retry, no dead-letter. A broker gives both; the in-process bus does not. |
| **No saga timeout.** | Nothing watches an order stuck in `Scheduled` awaiting a reply that never came. The SLA sweeper watches *due dates*, which for a Low-priority order could be days later. |
| **The overlap check races.** | Two concurrent bookings can both pass it and both insert. The fix is a database constraint, and there is no database yet. |

## What would make me revisit this

Written down now, so the decision is falsifiable rather than a matter of taste:

- A module needs to scale independently, or has a materially different load profile.
- Two or more teams are working in the codebase and their deploys block each other.
- One module's failure or resource use degrades the others in production.
- A boundary has survived six months without moving — evidence it is right enough to be worth
  making expensive.

Until one of those is true, the answer is this one.
