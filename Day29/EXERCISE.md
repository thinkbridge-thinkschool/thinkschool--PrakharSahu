# Day 29 — Build day 1: foundation + happy path

> **Exercise:** Paste the repo URL + the commit log for the day. Show the happy path working
> (a short clip or curl/UI walkthrough).

| | |
|---|---|
| Repo | `github.com/thinkbridge-thinkschool/thinkschool--PrakharSahu` · branch `feature/day29` |
| Commits today | **7** |
| Tests | **58 passed, 0 failed** — unchanged from the design day |
| Happy path | **8 passed, 0 failed** against Azure SQL, re-run 3× |
| Database | `sql-dispatch-dev-zgdsji` / `dispatch` — three schemas |

---

## What build day 1 was

From the [Day 28 build plan](../Day28/docs/build-plan.md), day 1:

> *Persistence — EF Core, **a schema per module**, not a shared context.
> **Exit:** the 58 existing tests pass unchanged against a real database.*

The happy path itself already existed: `Dispatch` was designed and scaffolded on Day 22 piece 2
with the full lifecycle over HTTP — raise, triage, schedule, start, log labour, complete, cancel.
What it did not have was anywhere to put the data. All three stores were dictionaries.

So today was not about new features. It was about making the thing that already worked work
**against real infrastructure**, and finding out what that broke.

---

## The commit log

```
199e6ae  Day 29: make the smoke script re-runnable against a real database
cb8654e  Day 29: fix a release that was never saved
f562a37  Day 29: wire the connection string and add the initial migrations
45127cb  Day 29: persist Scheduling and Billing, each in its own schema
e22ebdb  Day 29: persist WorkManagement with EF Core, one context per module
b8a5598  Day 29: start build day 1 from the Day 22 design scaffold
```

Committed in that order deliberately. The first commit is the scaffold **unchanged**, so every
later diff reads against a known-good state instead of against a copy-and-edit. The last two are
bug fixes that the day's work exposed, kept separate from the work that exposed them.

---

## 1. One DbContext per module

The Day 28 plan called for this explicitly, and it is a **correction rather than a preference**.
The Day 27 conversion kept one shared `AppDbContext` holding every module's entities, which meant
every module's Infrastructure transitively saw every other module's tables. It was bounded by an
architecture test and it was still the weakest part of that result.

```
workmanagement.WorkOrders          WorkManagementDbContext
workmanagement.WorkOrderLabour
workmanagement.__EFMigrationsHistory

scheduling.Reservations            SchedulingDbContext
scheduling.__EFMigrationsHistory

billing.Invoices                   BillingDbContext
billing.__EFMigrationsHistory
```

Scheduling cannot write a work order even by accident now, because the type is not reachable from
its context. The boundary stopped being a rule people have to remember.

All three point at the **same database**, separated by schema — one connection string, one backup
story, and the split can be made physical later if a module ever needs its own server.

Each module also gets its **own migrations history table**. Sharing one would make three
independently-migratable modules share a single append-only log, so `migrations add` in one module
would see the others' migrations as pending and try to apply them.

## 2. Mapping an aggregate that was designed before any database existed

None of the domain was bent to suit EF. The configuration is longer than a naive mapping and the
domain stayed clean in exchange.

| Domain decision | What the mapping had to do |
|---|---|
| `WorkOrderId`, `CustomerId`, `TechnicianId` are `readonly record struct` wrappers, so a customer id can never be passed where a technician id is expected | Value converters unwrap them. `ValueGeneratedNever`, because the domain creates the v7 GUID — a database-generated key would mean an aggregate is not fully formed until saved, and `Raise` would have nothing to put in the event it publishes |
| `ServiceAddress` and `ScheduledWindow` are records with private constructors and `Result`-returning factories | `OwnsOne`, binding through the private constructor, so the factories stay the only public way to build one |
| `Labour` is exposed only as `IReadOnlyList` and mutated only inside `LogLabour` | `OwnsMany` reached through the `_labour` backing field, so `LogLabour` remains the only way in |
| `Status` and `Priority` are enums | Stored as **strings**. `status = 3` means nothing during an incident, and renumbering the enum silently rewrites history |
| `Money` carries a currency because a bare decimal is not money | `OwnsOne` with `decimal(19,4)`, not EF's default `(18,2)`. Two places is enough to *store* a currency and not enough to *compute* one |

**`GetBreachingSlaAsync` narrows in SQL and decides in the domain.** The database filters to rows
that *could* have breached — a due date in the past, a non-terminal status — and
`WorkOrder.HasBreachedSla` makes the actual call. Pushing that rule into the LINQ predicate would
put the definition of "breached" in two places and let them drift. The SQL filter is deliberately
looser, and must never exclude a row the domain would have accepted.

## 3. Two bugs the move exposed

Both were correct only because of an accident of the previous implementation. Neither was caught
by any of the 58 tests.

### A release that was never saved

`WorkOrderReleasedHandler` loaded a reservation, called `Release()`, and stopped.

Against a dictionary that worked: the object in the dictionary **was** the entity, so mutating it
was instantly visible. Against EF the change sits in the change tracker and is discarded when the
scope ends. The slot was never released, the technician stayed booked, and **nothing threw** — the
only symptom was a later rebooking coming back refused.

`IReservationRepository` now has an explicit `SaveChangesAsync`. `WorkOrderService` was audited
for the same mistake and does not have it: every mutation goes through `MutateAsync`, which saves
and *then* publishes.

> Worth recording how this was found. Every unit test uses the dictionary fake, where the bug
> cannot exist — **a fake that cannot reproduce a failure mode cannot warn you about it.** It took
> driving the real thing over HTTP against a real database to see it, which is exactly what this
> exercise asks for.

### In-memory stores that lived in production code

The three `InMemory*Store` classes were in the **Infrastructure** projects, and the application
tests referenced those projects to borrow them. Deleting one broke the test build rather than
anything real.

A test double belongs to the test. They moved into the test project as fakes, and the three
Infrastructure project references are gone from the test csproj with them.

## 4. A smoke test that only passed on an empty database

The script passed 8/8 on the design day, then failed three assertions on its second run today
with nothing wrong in the code. Two causes, both artefacts of the stores having been dictionaries:

- **A fixed technician GUID.** Every run booked the same technician for the same window. Once the
  database remembered the first run, the second was correctly refused and compensated back to
  `Triaged`. Now a fresh GUID per run.
- **`console.log` of a number.** Node applies `util.inspect` formatting and **colours** numbers
  when it thinks stdout is a TTY, so the invoice count arrived as `ESC[33m0ESC[39m` and
  `[ "$COUNT" = "0" ]` was false while the value was right. Environment-dependent, which is the
  worst kind — it passed on the machine it was written on.

---

## The happy path working

`scripts/smoke.sh` drives the whole system over HTTP against Azure SQL. Full output in
[`docs/smoke-output.txt`](docs/smoke-output.txt).

```
 1. Raise a work order
   [PASS] created
 2. The state machine refuses out-of-order transitions
   [PASS] 409 Conflict, not 400 - the request was fine, the state was not
   [PASS] cannot schedule an untriaged order
 3. Triage derives the SLA due date
   [PASS] priority set, due date derived from it
 4. Scheduling crosses the module boundary
   [PASS] WorkManagement -> Scheduling reserved the slot
 5. A clashing booking is compensated back to triage
   [PASS] same technician, same window -> compensated back to Triaged
 6. Complete the first order, and Billing invoices it
   [PASS] nothing invoiced - no order has been completed
 7. Cancelling releases the technician's slot
   [PASS] the released slot was reusable - Scheduling heard the cancellation

 8 passed, 0 failed
```

Re-run twice more back to back against the same database — `8 passed, 0 failed` each time. That
matters more than the first pass: it is the difference between working and being repeatable.

### The proof that it is really a database

A work order created by one run, fetched from a **process that never created it**:

```json
{
  "id": "01a0a91e-c1ab-78db-abad-75387cf0bc8a",
  "status": "Cancelled",
  "summary": "Chiller unit is not holding temperature",
  "address": "Unit 4, Example Industrial Estate, Testville TV1 9ZZ",
  "priority": "High",
  "dueBy": "2026-09-17T07:29:20.2991674+00:00",
  "technicianId": "e078cce2-1863-4fb8-87ee-35f9d7aea3a3",
  "window": { "start": "2026-09-16T08:29:20.087+00:00",
              "end":   "2026-09-16T09:29:20.162+00:00" }
}
```

The aggregate, its owned `window` value object and its technician id all came back from SQL. And
the provider is not in doubt:

```
Provider name: Microsoft.EntityFrameworkCore.SqlServer
Data source:   tcp:sql-dispatch-dev-zgdsji.database.windows.net,1433
```

### Run it yourself

```bash
cd Day29
export ConnectionStrings__Dispatch="Server=tcp:sql-dispatch-dev-zgdsji.database.windows.net,1433;\
Initial Catalog=dispatch;Encrypt=True;Connection Timeout=60;Authentication=Active Directory Default"

dotnet test              # 58 passed
bash scripts/smoke.sh    # 8 passed, 0 failed
```

`Active Directory Default` picks up the `az` CLI login locally and a managed identity when
deployed. The server is Entra-only, so there is no password to configure either way.

---

## What is still a dictionary, and what is next

Nothing. All three stores are real.

Still open, in build-plan order:

- **The overlap check races** (day 2). Two concurrent bookings can both pass it and both insert.
  The fix is a database constraint, not more C# — and the reservation-failed path that handles it
  already exists.
- **Publish-after-commit is two operations** (day 3). A crash between them loses the event
  silently. The transactional outbox is the fix.
- **`ListAsync` is unbounded**, backing a demonstration endpoint. Paging belongs with the read
  models on day 7, where the query side gets designed rather than improvised.
- **Migrations are applied by hand.** Fine for one developer; it becomes a deployment step on
  day 9.
