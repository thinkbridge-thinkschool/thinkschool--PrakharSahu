# Day 22 piece 2 — what I built, where, and why

This piece is a **new solution**, not a delta on a previous day. So rather than "what changed",
this lists every file that carries a design decision, the exact type or method it lives in, what
the concept actually *is*, and why it was done that way.

Nothing from Day 17–22 piece 1 was touched.

---

## Definitions first

**Bounded context** — a boundary inside which one model of a word applies. "Technician" means
three different things in this system, and each context gets its own model. The alternative, one
shared class carrying every field any context ever needed, satisfies none of them and cannot be
changed by any of them alone. A bounded context is a *linguistic* boundary before it is a
technical one.

**Aggregate** — a cluster of objects treated as one unit for data changes, with one **aggregate
root** as the only entry point. Crucially it is a **transactional consistency boundary**:
everything inside is saved in one transaction and its invariants hold at every commit; everything
outside is referenced by id and reached eventually. The test for membership is *"does an invariant
require this to be consistent at the moment of commit?"* — not *"does this feel related?"*

**Entity vs value object** — an entity has identity and is compared by id; a value object is
compared by its values. Two work orders with identical fields are two different work orders. Two
addresses with identical fields are the same address. Getting this backwards produces aggregates
that cannot be told apart in a collection.

**Domain event vs integration event** — a domain event is how a module talks to *itself*: internal,
free to name the module's own types, free to change whenever the module changes. An integration
event is how it talks to *everyone else*: a published contract carrying primitives, versioned,
breaking to change. The translation between them happens at the module edge, on purpose, because
**that translation is where the coupling stops**.

**Clean / onion architecture** — dependencies point inwards. `Infrastructure → Application →
Domain`. Ports are declared by the layer that needs them and implemented by the layer that knows
how. Invert that one arrow and the layers keep their names and lose their value.

**Modular monolith** — real module boundaries (versioned contracts, no access to internals,
enforced by the build) inside a single deployable. The boundaries are genuine; the network between
them is not.

**Saga / compensating action** — two modules cannot share a transaction, so a multi-module
operation commits in stages and undoes itself with an explicit business operation when a later
stage refuses. Not a rollback — a *correction*.

---

## 1. The shared kernel

**Files:** `src/Dispatch.SharedKernel/{Result,Entity,Events,Messaging}.cs`

### `Result` — failure as a return value

```csharp
public class Result
{
    public bool IsSuccess { get; }
    public Error Error { get; }
    public static Result Failure(Error error) => new(false, error);
}

public sealed record Error(string Code, string Message);
```

**Why not exceptions:** *"You cannot complete a work order that was never started"* is not
exceptional. It is the domain working correctly, and it will happen thousands of times a day from
stale UIs. Exceptions stay for things that genuinely should not happen — a null argument, a lost
connection, a bug.

The practical difference: a `Result` is in the method signature, so a caller cannot forget it
exists. A thrown `InvalidOperationException` is invisible until production.

**Why `Error` has a `Code`:** it is the stable half. Message text gets reworded, shortened,
translated; a caller that switches on message text breaks when somebody fixes a typo. The HTTP
mapping in `WorkOrderEndpoints.Problem` keys on the code, never the message.

**Why the constructor throws on an inconsistent Result:** a failed result with no error, or a
success carrying one, means a caller will read a field that says nothing. Better to fail at
construction.

### `Entity<TId>` and `AggregateRoot<TId>`

```csharp
public abstract class AggregateRoot<TId> : Entity<TId>
{
    private readonly List<IDomainEvent> _domainEvents = [];
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;
    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
    public void ClearDomainEvents() => _domainEvents.Clear();
}
```

**Why the aggregate collects events rather than publishing them:** publishing from inside would
announce a decision the transaction may still roll back, and a handler that has already emailed
the customer cannot un-email them. Collecting also keeps the domain ignorant of messaging
entirely, which is what lets it be tested without a broker — see
`Dispatch.WorkManagement.Domain.Tests`, where every test is a `new` and a fake clock.

**Why `protected Entity()` exists:** EF Core's materialiser needs a way in that does not run the
constructor's invariants. It is `protected`, not `public`, because application code reaching for
it is constructing an entity in an invalid state.

### `IClock`

**Why time is injected:** this domain is full of time-dependent rules — SLA due dates, scheduled
windows, "you cannot start before the window opens". Every one is untestable if the aggregate
reads `DateTimeOffset.UtcNow` itself, because the test would have to wait for real time to pass.
`TestClock` in the domain tests advances it by three days in a line.

### `IIntegrationEventPublisher`

**Why the interface exists at all:** it is the seam that lets this stay a monolith today and stop
being one later. Swapping the in-process dispatcher for a Service Bus topic is a change to one
class in the composition root, because no module has ever been allowed to know which it was
talking to.

---

## 2. The core aggregate

**File:** `src/Modules/WorkManagement/Dispatch.WorkManagement.Domain/WorkOrders/WorkOrder.cs`

### The boundary

**Inside:** the order's state, its scheduled window, its labour entries.
**Outside, by id:** technician, customer, invoice.

| Excluded | Why |
|---|---|
| **Technician** | Pulling it in means loading a technician to save a work order, locking that row against every other order being saved at that moment. A busy technician becomes the system's hottest write lock — for a rule that is not even about them. |
| **Customer** | Belongs to a CRM context this system does not own. Referenced, never validated here. |
| **Invoice** | Created *after* completion, by a subscriber. A work order that could not be completed because the invoicing service was down would couple a field engineer's day to a ledger. |

**Included: `LabourEntry`.** It has identity (you can correct one entry without touching the
others) but no meaning outside its work order. Keeping it inside is what makes *"cannot complete
with no labour logged"* answerable **without a query** — move it out and the check becomes a race.

Every extra entity pulled inside is another row locked in the same transaction. Aggregates are
almost always smaller than they are first drawn.

### The state machine

```csharp
public Result Triage(WorkOrderPriority priority, IClock clock)
{
    if (Status != WorkOrderStatus.Raised)
        return Result.Failure(WorkOrderErrors.WrongStatus("triage", Status, WorkOrderStatus.Raised));

    Priority = priority;
    DueBy = RaisedAt + SlaTargets[priority];
    Status = WorkOrderStatus.Triaged;

    Raise(new WorkOrderTriaged(Id, priority, DueBy.Value));
    return Result.Success();
}
```

**Why an enum and not booleans:** three booleans (`IsScheduled`, `IsComplete`, `IsCancelled`)
describe eight states, five of which are nonsense — "cancelled and complete" is representable and
meaningless. One enum describes exactly the states that exist.

**Why the due date is computed here, once:** recalculating on read would silently move every
existing order's deadline the day the SLA table changes, including orders that have already
breached.

**Why it runs from `RaisedAt`, not from triage:** the customer's clock started when they called.
Measuring from triage lets the SLA be reset by the company's own slowness — which is exactly the
delay it is supposed to be measuring. Tested by
`The_due_date_runs_from_when_the_problem_was_reported_not_when_it_was_triaged`.

**Why `SlaTargets` is a dictionary in the domain:** "how urgent is urgent" is a business rule, not
configuration — changing it changes what the company has promised its customers.

### The invariant the whole boundary exists for

```csharp
public Result Complete(IClock clock)
{
    if (Status != WorkOrderStatus.InProgress) return Result.Failure(...);

    if (_labour.Count == 0)
        return Result.Failure(WorkOrderErrors.NoLabourLogged);

    CompletedAt = clock.UtcNow;
    Status = WorkOrderStatus.Completed;
    var withinSla = DueBy is null || CompletedAt <= DueBy;
    Raise(new WorkOrderCompleted(Id, CustomerId, TotalLabourMinutes, withinSla, CompletedAt.Value));
    return Result.Success();
}
```

A completed order with no labour is either an unbillable job or a forgotten timesheet, and both
are worth catching at completion rather than in a month-end reconciliation.

**Why `LogLabour` is restricted to `InProgress`:** completion emits a total that Billing turns
into money. Letting that total change afterwards means the invoice and the work order disagree,
with no event to reconcile them.

**Why `Cancel` refuses a completed order:** the completion event has already left the module.
Reversing it is a credit note, which is Billing's decision, not a state change this aggregate can
make on its behalf.

**Why `HasBreachedSla` is a method and not a stored flag:**

```csharp
public bool HasBreachedSla(DateTimeOffset now) =>
    DueBy is { } dueBy && now > dueBy
    && Status is not (WorkOrderStatus.Completed or WorkOrderStatus.Cancelled);
```

Breach is a function of the clock. Storing it means a background job writing to every open order
just so a boolean stays honest — and being wrong in between runs.

### Strongly-typed identifiers

**File:** `.../WorkOrders/Identifiers.cs`

```csharp
public readonly record struct WorkOrderId(Guid Value);
public readonly record struct CustomerId(Guid Value);
public readonly record struct TechnicianId(Guid Value);
```

Four Guids in this domain, all mutually assignable if left bare. `Complete(customerId)` where
`Complete(technicianId)` was meant compiles perfectly and fails at runtime with a "not found" that
names nothing. `readonly record struct` costs no allocation and gets value equality free.

---

## 3. Value objects

**File:** `.../WorkOrders/ValueObjects.cs`

```csharp
public sealed record ServiceAddress
{
    private ServiceAddress(string line, string city, string postcode) { ... }
    public static Result<ServiceAddress> Create(string? line, string? city, string? postcode) { ... }
}
```

**Why a private constructor plus a static factory:** validation lives in one place and there is no
second route in. An invalid address cannot be constructed anywhere in the system — including by a
test, a deserialiser or a well-meaning mapper.

**Why `ScheduledWindow` is a window and not a start time:** a window is what is actually promised
to a customer ("someone will arrive between 9 and 11"). Modelling it as a start time and hoping
everyone remembers the implied duration is how a scheduling system double-books.

**Why `start < now` is rejected:** scheduling into the past is always a bug — clock skew, a
timezone mistake, a stale form. Rejecting it here means the rest of the aggregate can trust that a
scheduled window has not happened yet.

---

## 4. The published contract

**File:** `src/Modules/WorkManagement/Dispatch.WorkManagement.Contracts/WorkManagementEvents.cs`

```csharp
public sealed record WorkOrderScheduledV1(
    Guid WorkOrderId, Guid TechnicianId,
    DateTimeOffset WindowStart, DateTimeOffset WindowEnd) : IntegrationEvent;
```

Three rules hold across the file, and they are what make the boundary real:

1. **Primitives only.** No `WorkOrderId`, no `ServiceAddress`, no `WorkOrderStatus`. A domain type
   here means every subscriber recompiles when WorkManagement renames a field — the exact coupling
   the boundary exists to prevent. Enforced by `Contracts_expose_primitives_only`.
2. **Versioned in the name.** `V1` is not decoration. A published event has subscribers that deploy
   on their own schedule, so a breaking change is a *new* event alongside the old one.
3. **Only what somebody needs.** WorkManagement raises **seven** domain events internally;
   **three** appear here. Publishing the rest "just in case" would create subscribers this module
   then owes compatibility to forever.

**Why `WorkOrderReleasedV1` covers both cancellation and return-to-triage:** from Scheduling's
point of view they are the same instruction — release the slot. Two events would mean two handlers
doing identical work, and one of them eventually being forgotten.

---

## 5. Use cases, and the module edge

**File:** `.../Dispatch.WorkManagement.Application/WorkOrders/WorkOrderService.cs`

Every mutation has the same four steps:

```csharp
private async Task<Result> MutateAsync(Guid id, Func<WorkOrder, Result> change, CancellationToken ct)
{
    var order = await repository.GetAsync(new WorkOrderId(id), ct);
    if (order is null) return Result.Failure(NotFound);

    var result = change(order);           // the DOMAIN decides
    if (result.IsFailure) return result;  // nothing saved, nothing published

    await unitOfWork.SaveChangesAsync(ct);
    await PublishAsync(order, ct);
    return Result.Success();
}
```

**Why there is no business logic here:** if a rule appears in this file — an `if` about status, a
date comparison, a "but only when" — it has escaped the aggregate, and the aggregate has stopped
being able to guarantee anything about itself. That is how a domain model quietly becomes an
anaemic bag of properties, and keeping this layer boring is how you notice it happening.

**Why no MediatR:** at kickoff a mediator is indirection with nothing to buy. There are no pipeline
behaviours yet, and `WorkOrders.CompleteAsync(...)` is greppable in a way that
`Send(new CompleteWorkOrderCommand(...))` is not. It can be introduced later behind exactly this
surface, the day cross-cutting concerns need somewhere to live.

### The translation — this is the module edge

```csharp
IIntegrationEvent? outbound = domainEvent switch
{
    WorkOrderScheduled e => new WorkOrderScheduledV1(...),
    WorkOrderCompleted e => new WorkOrderCompletedV1(...),
    WorkOrderCancelled e => new WorkOrderReleasedV1(e.WorkOrderId.Value, e.Reason),
    WorkOrderReturnedToTriage e => new WorkOrderReleasedV1(e.WorkOrderId.Value, e.Reason),
    _ => null    // Raised, Triaged, Started stay inside
};
```

**The mapping is explicit and lossy on purpose.** Anything not translated here cannot leave the
module, which is what lets WorkManagement rename, split or delete an internal event without asking
anybody. Tested by `Only_the_three_documented_events_ever_leave_WorkManagement`.

### The re-entrancy fix — a real bug a test found

```csharp
// Snapshot and clear BEFORE publishing, not after.
var pending = order.DomainEvents.ToArray();
order.ClearDomainEvents();

foreach (var domainEvent in pending) { ... }
```

The original code iterated `order.DomainEvents` and cleared afterwards. It threw *"Collection was
modified"*, because the scheduling saga re-enters this method on the same aggregate instance:

```
Schedule()  → publish WorkOrderScheduledV1
            → Scheduling sees a clash, publishes TechnicianReservationFailedV1
            → WorkManagement's handler calls ReturnToTriageAsync on THIS order
            → the aggregate raises another domain event
            → ...into the list this loop is still iterating
```

**Why it happened at all:** in-process dispatch makes a publish synchronous and therefore
re-entrant. A broker would not reproduce it — the compensating event would arrive in a later
request on a freshly loaded aggregate. That difference is the real cost of an in-process bus, and
it argues for keeping handlers re-entrancy-safe rather than relying on a transport that happens to
serialise them today.

Caught by `A_double_booked_technician_sends_the_order_back_to_triage`.

---

## 6. The async flows

### Flow 1 — the scheduling saga

**Files:** `.../Dispatch.Scheduling.Application/Reservations/ReservationHandlers.cs`,
`.../Dispatch.WorkManagement.Application/WorkOrders/ReservationFailedHandler.cs`

```csharp
public async Task HandleAsync(WorkOrderScheduledV1 e, CancellationToken ct = default)
{
    var existing = await reservations.GetByWorkOrderAsync(e.WorkOrderId, ct);
    if (existing is not null) return;                    // duplicate delivery

    if (await reservations.HasOverlapAsync(technicianId, e.WindowStart, e.WindowEnd, ct))
    {
        await publisher.PublishAsync(new TechnicianReservationFailedV1(...), ct);
        return;
    }
    ...
}
```

**Why the handler is allowed to say no:** WorkManagement asserted *intent*, not fact. It has no
visibility of the technician's calendar and never should. Scheduling is the only module that can
answer "is this person free", and it answers asynchronously, after the work order has committed.

**Why the duplicate check is a lookup rather than a flag:** finding the existing reservation is
both the duplicate check and the answer. Naive overlap checking would see the reservation *this
event already created*, call it a clash, and bounce a perfectly good order back to triage — tested
by `A_redelivered_schedule_does_not_double_book_or_falsely_fail`.

**Why `ReturnToTriage` is a domain operation and not a field reset:** "we told you we were coming
and now we are not" is a real business event that dispatchers and customers need to hear about.

**Why the failure handler logs instead of throwing:**

```csharp
if (result.IsFailure)
{
    logger.LogInformation("Work order {WorkOrderId} was not returned to triage ({Code}). "
        + "Expected when the order was already cancelled or the event was redelivered.", ...);
}
```

Throwing tells the transport to redeliver an event that will fail identically forever. It also
makes the handler idempotent by construction: the second delivery finds the order already in
Triaged and the aggregate refuses the transition, which is exactly the desired no-op.

### Flow 2 — Billing

**File:** `.../Dispatch.Billing.Application/Invoices/DraftInvoiceHandler.cs`

**Why asynchronous:** a field engineer taps "done" on a phone with two bars of signal. If
completing the job required Billing to price and store an invoice in the same request, an
accounting problem would stop engineers finishing work.

**Why everything travels on the event:** so Billing never calls back into WorkManagement. A
synchronous query across a module boundary is a synchronous coupling wearing an asynchronous
costume, and it fails the moment the two are deployed separately.

**Why the idempotency check matters most here:** a redelivered completion event invoices the
customer twice, which is the most expensive duplicate this system can produce. Tested by
`A_redelivered_completion_does_not_invoice_the_customer_twice`.

**Where the pricing rule lives:** `Invoice.Draft` in Billing's domain, and nowhere else.
WorkManagement reports minutes; what a minute costs is not its business, and a work order that
knew the hourly rate would need redeploying every time finance changed it.

### Flow 3 — the SLA sweeper

**File:** `.../Dispatch.WorkManagement.Infrastructure/SlaSweeper.cs`

**Why it needs a timer instead of a subscription:** the other two flows react to something someone
caused. This one reacts to *nothing happening*, which is the whole problem with deadlines.

**Why a scope per tick:** the sweeper is a singleton and the repository is scoped. Capturing one at
construction would hold a single scope open for the process lifetime.

**Why the loop swallows exceptions:** an unhandled exception in a `BackgroundService` takes the
whole host down by default, and a transient read failure is not a reason to stop serving HTTP.

---

## 7. The composition root

**Files:** `src/Dispatch.Api/Program.cs`,
`src/Dispatch.Api/Messaging/InProcessIntegrationEventPublisher.cs`

```csharp
builder.Services.AddSingleton<IIntegrationEventPublisher, InProcessIntegrationEventPublisher>();
builder.Services.AddWorkManagement();
builder.Services.AddScheduling();
builder.Services.AddBilling();
```

**Why each module registers itself:** the host calls one method and knows nothing about what is
inside. Adding, removing or extracting a module is a one-line change rather than archaeology
through `Program.cs`.

**Why a fresh DI scope per event in the publisher:** a handler running inside the publishing
request's scope would share its unit of work, which would quietly re-couple the two modules
through a transaction neither declared.

**Why a failing handler is caught and logged:** one failing subscriber must not stop the others,
and must not fail the publisher — which is still inside the caller's request. That is the same
isolation a real broker gives per-subscription, reproduced so that moving to one later does not
change the failure semantics. The honest limitation, stated in the code: **the event is lost**. A
broker would retry and eventually dead-letter it.

**Why endpoints live in the host rather than in each module:** stated as a tradeoff rather than
hidden. It costs a little module autonomy; the alternative is a Presentation project per module
with a `FrameworkReference` to ASP.NET Core, which buys that back at the cost of three more
projects and a web framework dependency inside every module. At three modules that is not worth
it. At ten it will be, and moving the files then is mechanical.

### The JSON enum fix — a defect the smoke test found

```csharp
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
```

Without it, `System.Text.Json` binds enums by their **numeric** value: `{"priority":"High"}` is a
400 and `{"priority":2}` is accepted. That makes the API's contract a set of magic numbers that
silently change meaning the day somebody inserts a new enum member in the middle.

The smoke test caught it four sections downstream, as "status is Raised" — because the script had
discarded triage's status code with `>/dev/null`. Both were fixed: the converter, and capturing
the code so the failure names itself.

---

## 8. The architecture tests — the point of the whole scaffold

**Files:** `tests/Dispatch.ArchitectureTests/{SolutionGraph,LayerDependencyTests,CompiledDependencyTests}.cs`

"Clean architecture" and "modular monolith" are claims about which things may know about which
other things. A claim written only in a README has already started decaying — nobody adds a
forbidden reference on purpose, they add it at 5pm because the type they needed happened to be
over there.

### Two complementary approaches

**`SolutionGraph`** parses the `.csproj` files. **`CompiledDependencyTests`** reflects over the
emitted assemblies.

They catch different mistakes, and the reason is worth knowing: **the C# compiler drops a project
reference the code never uses.** So reflection answers "what does this depend on today" — which
would let somebody *declare* a forbidden reference and stay green until the first line of code used
it. The declaration is the architectural decision, so the declaration is what gets tested. Meanwhile
a NuGet package that drags a framework into the domain transitively only shows up in reflection.

### The twelve rules

| Test | Stops |
|---|---|
| `Domain_depends_on_nothing_but_the_shared_kernel` | a domain that needs a database to be tested |
| `Application_never_references_infrastructure` | the inverted arrow that makes layers decorative |
| `Contracts_depend_on_nothing_but_the_shared_kernel` | one module's packages becoming everyone's |
| `No_module_reaches_into_another_modules_internals` | two modules wearing two folder names |
| `Only_the_host_composes_infrastructure` | a module reaching for another's database |
| `The_shared_kernel_depends_on_nothing` | a shared kernel that grows dependencies |
| `Every_module_has_the_same_four_layers` | a module that put something in the wrong one |
| `The_documented_cross_module_edges_are_the_only_ones` | a coupling nobody discussed |
| `No_domain_assembly_knows_about_a_database_a_web_framework_or_a_broker` | transitive infrastructure |
| `Contracts_expose_primitives_only` | a published event that freezes the internal model |
| `Domain_events_never_leak_into_a_published_contract` | internal events becoming public API |
| `Aggregate_roots_have_no_public_setters` | invariants downgraded to suggestions |

### The inventory test is the interesting one

```csharp
var allowed = new HashSet<string>
{
    "Dispatch.Scheduling.Application -> Dispatch.WorkManagement.Contracts",
    "Dispatch.Billing.Application -> Dispatch.WorkManagement.Contracts",
    "Dispatch.WorkManagement.Application -> Dispatch.Scheduling.Contracts"
};
Assert.Equal(allowed.Order(), actual.Order());
```

The complete inter-module dependency graph, written down. If it fails, either somebody added a
coupling that was never discussed, or the design moved and `DESIGN.md` has not caught up. **Both
are worth stopping for.**

### Proof that they fail

`docs/architecture-guardrail-proof.txt` adds `WorkManagement.Domain → Scheduling.Domain` and
captures three rules catching it. Overlap is deliberate: one rule can be weakened by a well-meaning
edit; three stating the same boundary in different terms cannot all be softened without somebody
noticing what they are doing.

### Two bugs in the tests themselves

- **Static initialiser ordering.** `Projects { get; } = Load()` ran before
  `Root { get; } = FindRoot()`, so every test failed with a null path inside a
  `TypeInitializationException` — an error naming neither the cause nor the file. Fixed with
  `Lazy<T>`, which removes the ordering question rather than relying on nobody reordering two lines.
- **`.slnx`, not `.sln`.** The .NET 10 SDK writes the XML-based solution format by default, so an
  exact-name lookup found nothing.

---

## 9. Tests

| Project | Count | Covers |
|---|---:|---|
| `Dispatch.ArchitectureTests` | 12 | the rules above |
| `Dispatch.WorkManagement.Domain.Tests` | 35 | every invariant and transition |
| `Dispatch.WorkManagement.Application.Tests` | 11 | the async flows across real boundaries |
| `scripts/smoke.sh` | 8 | the running host over real HTTP |

**Why the domain tests need no container, database, mock or web host:** because the architecture
tests keep it that way. `TestClock` is the entire fixture.

**Why the flow tests use real classes from all three modules:** they prove the modules actually
*compose*, not that each behaves correctly in isolation against a fake version of its neighbour.
The only substitutions are the transport and the clock.

**Why `TestBus` dispatches synchronously and depth-first:** it makes a whole saga deterministic
inside one `await`, which is the only reason these tests can assert on an end state without
polling or sleeping. The tests are careful not to depend on ordering a broker would not guarantee.

---

## Files

| Path | What it carries |
|---|---|
| `src/Dispatch.SharedKernel/Result.cs` | failure as a return value |
| `src/Dispatch.SharedKernel/Entity.cs` | entity/aggregate, event collection |
| `src/Dispatch.SharedKernel/Events.cs` | domain vs integration events, `IClock` |
| `src/Dispatch.SharedKernel/Messaging.cs` | the transport seam |
| `.../WorkManagement.Domain/WorkOrders/WorkOrder.cs` | **the core aggregate** |
| `.../WorkManagement.Domain/WorkOrders/ValueObjects.cs` | status, priority, address, window, labour |
| `.../WorkManagement.Domain/WorkOrders/Identifiers.cs` | strongly-typed ids |
| `.../WorkManagement.Contracts/WorkManagementEvents.cs` | **the published contract** |
| `.../WorkManagement.Application/WorkOrders/WorkOrderService.cs` | use cases + **the module edge** |
| `.../WorkManagement.Application/WorkOrders/ReservationFailedHandler.cs` | the compensating action |
| `.../WorkManagement.Infrastructure/SlaSweeper.cs` | flow 3 |
| `.../Scheduling.Domain/Reservations/Reservation.cs` | the second aggregate |
| `.../Scheduling.Application/Reservations/ReservationHandlers.cs` | flow 1 |
| `.../Billing.Domain/Invoices/Invoice.cs` | the third aggregate, `Money` |
| `.../Billing.Application/Invoices/DraftInvoiceHandler.cs` | flow 2 |
| `src/Dispatch.Api/Program.cs` | composition root |
| `src/Dispatch.Api/Messaging/InProcessIntegrationEventPublisher.cs` | **the exit hatch** |
| `src/Dispatch.Api/Endpoints/WorkOrderEndpoints.cs` | HTTP, error-code → status mapping |
| `tests/Dispatch.ArchitectureTests/` | **the rules, enforced** |
| `tests/Dispatch.WorkManagement.Domain.Tests/` | 35 invariants |
| `tests/Dispatch.WorkManagement.Application.Tests/` | 11 flows |
| `scripts/smoke.sh` | 8 HTTP assertions |
| `scripts/build-screenshot-cards.mjs` | screenshots, generated from `docs/` |
| `DESIGN.md` | the one-page design |
