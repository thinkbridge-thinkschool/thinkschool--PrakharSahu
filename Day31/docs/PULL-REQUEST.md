# Build day 2: close the double-booking race and the never-ending saga

> Paste this as the PR description. It is written for a reviewer who has not read the day's notes.

## What this does

Day 29 made the happy path real. This makes two invariants actually hold — one under concurrency,
one under silence. Both gaps were already written down; neither is new information.

| | Invariant | How it was failing |
|---|---|---|
| 1 | A technician is never double-booked | Two concurrent requests both pass the overlap check and both insert |
| 2 | A scheduled order eventually resolves | Scheduling never replies; the order sits in `Scheduled` for ever |

**76 tests passing, up from 58.** Smoke: 8 passed, 0 failed.

---

## 1. Double-booking

`HasOverlapAsync` then `AddAsync` is a check-then-act race. Replaced on the write path by
`TryHoldAsync`, which does both inside one **serializable** transaction — SQL Server takes range
locks over the rows the overlap query examined, including rows that do not exist yet, so a
concurrent insert into that range blocks rather than succeeding.

Backstopped by making `IX_Reservations_Technician_Window` **unique**, filtered on
`[IsReleased] = 0` so a released slot can be rebooked with the identical window.

`HasOverlapAsync` stays, for reads that only want to look. Its doc comment now says so instead of
claiming the race is unfixed.

**Limit, stated in the code:** the unique index catches an *exact* duplicate window only. SQL
Server has no exclusion constraint, so partial overlap is the transaction's job alone.

### Proof it catches the bug

`TryHoldAsync` was temporarily reverted to the old shape and the suite re-run against the same real
SQL Server (`docs/concurrency-proof.txt`):

```
--- WITHOUT the fix ---
  Two live reservations overlap: 10:33 AM-12:33 PM and 10:13 AM-12:13 PM
  Failed!  - Failed: 2, Passed: 1

--- WITH the fix ---
  Passed!  - Failed: 0, Passed: 3
```

The assertion is the invariant itself, not a proxy. The **partial** overlap case fails first —
the one the index cannot catch — so weakening the isolation level is caught rather than tolerated.

---

## 2. The saga with no ending

`TechnicianReservedV1` was published on every successful hold and **nothing subscribed to it**. The
failure reply had a handler; the success reply went nowhere. A work order in `Scheduled` therefore
looked identical whether Scheduling had confirmed, refused, or never answered.

- `ReservationConfirmedHandler` — new; subscribes to the reply that went nowhere
- `WorkOrder.ConfirmReservation` / `ReservationConfirmedAt` — records it, idempotently
- `WorkOrder.ReservationDeadline` — set at schedule time, `now + 2 minutes`
- `ReservationTimeoutSweeper` — new; every minute, returns stuck orders to triage

`SlaSweeper` does not cover this: it watches **due dates**, and a Low-priority order has ten days
of SLA, so a booking that silently failed would surface a week and a half later as a breach.

---

## Review notes — the choices I expect questions about

**`Schedule` now takes an `IClock`.** A deadline needs a time, and every other transition that
needs one already takes it. The alternative — reading `DateTimeOffset.UtcNow` inside the aggregate
— is what makes a domain untestable.

**`ConfirmReservation` accepts a confirmation that arrives after work started.** Under a broker the
reply is asynchronous, so a dispatcher can press "start" in the gap. Refusing it would discard true
information to satisfy a state machine and leave the order permanently unconfirmed.

**The timeout sweeper acts; the SLA sweeper only reports.** There is one obviously correct action
here — `ReturnToTriage`, the same compensation an explicit refusal triggers. An SLA breach has no
single answer, so reporting is the honest limit there.

**The sweeper routes through `WorkOrderService`, not the repository.** That is what dispatches the
domain events: `ReturnToTriage` raises `WorkOrderReturnedToTriage` and Scheduling releases the slot.
A hand-rolled save would have skipped it silently.

**A new test project that needs a database.** The race lives in the database's semantics; a fake
closes it with a `lock`, which proves the fake is good and nothing else. Skips with a reason when
`ConnectionStrings__Dispatch` is unset — a test that cannot run is not a test that passed.

---

## Migrations

Two, both additive:

- `WorkManagement/AddReservationTimeout` — two nullable columns
- `Scheduling/MakeReservationWindowUnique` — drops and recreates the index as unique

**The second can fail on a database that already contains overlapping reservations.** On this dev
database it applied cleanly. On anything with real data it needs a check first — see "Deployment
note" below.

## Deployment note

```sql
-- Run before applying MakeReservationWindowUnique against a database with real data.
SELECT TechnicianId, Start, [End], COUNT(*)
FROM scheduling.Reservations
WHERE IsReleased = 0
GROUP BY TechnicianId, Start, [End]
HAVING COUNT(*) > 1;
```

Any rows returned must be resolved before the migration will apply.

## Not in this PR

- The transactional outbox (day 3) — publish-after-commit is still two operations
- Swapping the in-process bus for a broker (day 4). Both features here are written against
  at-least-once asynchronous delivery, but only the concurrency test proves anything about a real
  transport.
