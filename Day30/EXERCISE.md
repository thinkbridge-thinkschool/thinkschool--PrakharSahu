# Day 30 — Build day 2: feature completeness

> **Exercise:** Paste the PR URL + a thread where you responded to review feedback. State what you
> changed and what you defended.

| | Day 29 | Day 30 |
|---|---:|---:|
| Tests | 58 | **76** |
| Test projects | 3 | 4 |
| Smoke assertions | 8 passed | **8 passed** |
| Known gaps closed | — | **2** |

Evidence: [`docs/concurrency-proof.txt`](docs/concurrency-proof.txt) ·
[`docs/test-results.txt`](docs/test-results.txt) · [`docs/smoke-output.txt`](docs/smoke-output.txt) ·
[`docs/architecture-guardrail-proof.txt`](docs/architecture-guardrail-proof.txt)

---

## What "feature complete" meant here

Day 29 made the happy path real: three modules, three databases, an order that goes from raised to
invoiced. Every endpoint worked.

What was **not** true is that the system's invariants held. Two of them failed in ways the happy
path cannot show:

| | The invariant | How it failed |
|---|---|---|
| **1** | A technician is never double-booked | Under concurrency. Two requests both pass the overlap check, both insert. |
| **2** | A scheduled order eventually resolves | Under silence. Scheduling never replies, the order sits in `Scheduled` for ever. |

Those are the two gaps this day closes. Both were already written down — the first as Day 29's own
known gap, the second as finding 1 of the Day 28 design review.

---

## 1. The double-booking race

### The bug

```csharp
if (await reservations.HasOverlapAsync(...))   //  <- both requests reach here
    return Conflict;                           //     and both see a free calendar
await reservations.AddAsync(reservation);      //  <- then both insert
```

Reads correctly. Wrong the moment two requests arrive together. Nothing throws, nothing logs, and
the first anybody knows is two vans at one address.

### The fix: two mechanisms, because one is not enough

**A serializable transaction** — `EfReservationRepository.TryHoldAsync`. Under `Serializable`, SQL
Server takes *range locks* over the rows the overlap query examined, including rows that do not
exist yet. A second transaction inserting into that range blocks until the first commits, and then
sees it. The gap between the check and the write is in the database's view of the world, so no
amount of C# can close it.

**A unique filtered index as a backstop** — `IX_Reservations_Technician_Window`, now `IsUnique()`
and filtered on `[IsReleased] = 0`. Range locks depend on an index and on the isolation level
actually being applied; if either assumption is ever wrong, the constraint still refuses the
duplicate.

The filter matters: without `[IsReleased] = 0`, cancelling a booking and rebooking the identical
window would collide with the dead row and permanently poison that slot.

**Honest limit, stated rather than implied:** the unique index only catches an *exact* duplicate
window. SQL Server has no exclusion constraint, so `09:00–11:00` versus `10:00–12:00` is the
transaction's job alone.

### The proof

A test that has never failed is a test nobody has checked. `TryHoldAsync` was temporarily reverted
to the old shape and the suite re-run against the same real SQL Server:

```
--- WITHOUT the fix ---
  Two live reservations overlap: 10:33 AM-12:33 PM and 10:13 AM-12:13 PM
  Failed!  - Failed: 2, Passed: 1

--- WITH the fix ---
  Passed!  - Failed: 0, Passed: 3
```

Full output in [`docs/concurrency-proof.txt`](docs/concurrency-proof.txt).

The failing assertion is the invariant itself — *no two live reservations for one technician may
overlap* — not a proxy for it. And it is the **partial** overlap case that fails first, which is
the one the index cannot catch, so weakening the isolation level is caught rather than tolerated.

### Why this test needs a real database

`Dispatch.Scheduling.Concurrency.Tests` is a new project that talks to SQL Server, because the race
lives in the database's semantics. The in-memory fake closes it with a `lock`, which proves the
fake is good and nothing else — and its doc comment now says exactly that.

This is Day 29's lesson applied before it bit again: a missing `SaveChanges` was invisible to every
unit test, because every unit test used a dictionary where mutating the stored object *was* the
save.

When `ConnectionStrings__Dispatch` is unset the tests **skip with a reason** rather than pass. A
test that cannot run is not a test that passed.

---

## 2. The saga that never ends

### The bug

Scheduling answers `TechnicianReservedV1` or `TechnicianReservationFailedV1`. The failure had a
handler. **The success reply was published and nobody subscribed.**

So a work order in `Scheduled` looked identical whether Scheduling had confirmed, refused, or never
answered at all. A dropped event or a crashed consumer left it there for ever — having already told
a customer somebody was coming.

`SlaSweeper` does not catch this. It watches **due dates**: a Low-priority order has ten days of
SLA, so a booking that silently failed surfaces a week and a half later as a breach rather than
immediately as a stuck saga.

### The fix

| Piece | What it does |
|---|---|
| `WorkOrder.ReservationDeadline` | Set at `Schedule` time — `now + ReservationGrace` (2 minutes) |
| `WorkOrder.ConfirmReservation` | Records the confirmation. Idempotent; a redelivery is a no-op |
| `ReservationConfirmedHandler` | **New.** Subscribes to the success reply that went nowhere |
| `WorkOrder.IsAwaitingReservation` | The predicate: `Scheduled`, unconfirmed, past deadline |
| `ReservationTimeoutSweeper` | **New.** Every minute, returns stuck orders to triage |

Two decisions worth defending:

**`ConfirmReservation` accepts a confirmation that arrives after work started.** Under a broker the
reply is asynchronous, so a dispatcher can press "start" in the gap. Refusing it then would throw
away true information to satisfy a state machine — and leave the order looking permanently
unconfirmed.

**The timeout sweeper acts; the SLA sweeper only reports.** There is one obviously correct action
here and it already exists: `ReturnToTriage`, the same compensation an explicit refusal triggers.
An SLA breach has no single answer — reassign, escalate, phone the customer — so reporting is the
honest limit there.

Routing through `WorkOrderService` rather than saving by hand is what gets the domain events
dispatched: `ReturnToTriage` raises `WorkOrderReturnedToTriage`, Scheduling releases any slot it did
hold, and a reply arriving after the timeout cannot leave a technician booked for an abandoned
order. A hand-rolled save would have skipped that silently.

---

## The tests

**76 passing**, up from 58.

| Project | Day 29 | Day 30 | What the new ones cover |
|---|---:|---:|---|
| `WorkManagement.Domain.Tests` | 35 | **44** | The deadline, confirmation idempotence, late confirmations, rescheduling after a timeout |
| `WorkManagement.Application.Tests` | 11 | **17** | Confirmation recorded across the boundary; a silent Scheduling; a late reply for a timed-out order |
| `Scheduling.Concurrency.Tests` | — | **3** | The race, against real SQL Server |
| `ArchitectureTests` | 12 | **12** | Unchanged — the boundaries did not move |

The nastiest test is `A_late_confirmation_for_a_timed_out_order_is_refused_not_silently_applied`.
Under a broker the confirmation can arrive after the timeout gave up; applying it would mark an
order confirmed whose slot has already been released — a booking nobody is holding.

---

## Still open

Unchanged from Day 29's list, minus the two closed above:

- **Publish-after-commit is two operations** (day 3). A crash between them loses the event
  silently. The transactional outbox is the fix.
- **`ListAsync` is unbounded**, backing a demonstration endpoint. Paging belongs with the read
  models on day 7.
- **Migrations are applied by hand.** It becomes a deployment step on day 9.
- **The in-process bus still makes delivery synchronous.** Both features added today are written
  against at-least-once, asynchronous delivery — but only the concurrency test proves anything
  about the real transport. That swap is day 4, and it is the day the ADR says will require
  re-examining every handler.
