# Design review — Dispatch

**What this is, plainly.** An adversarial review of `Day22/piece2/DESIGN.md` and the code behind
it, carried out by Claude at my request. It is **not** mentor or peer feedback, and it is labelled
that way deliberately: the exercise asks for a critique that changed the design, and a fabricated
quote from a mentor would make the ADR that follows dishonest at its foundation.

Every finding below was checked against the code, not inferred from the document.

---

## The top critique

> **"The exit is already built" is not true, and your own bug report is the proof.**

`DESIGN.md` closes its central argument with this:

> *The exit is already built. `InProcessIntegrationEventPublisher` is one class in the host. It
> gets replaced with a Service Bus topic and no module changes, because no module was ever allowed
> to know which it was talking to.*

The claim is that the transport is an implementation detail. The design then, four lines later,
documents a bug whose existence depends entirely on which transport is in use:

> *an in-process bus makes publishes synchronous and therefore re-entrant — a scheduling failure
> comes back into the same aggregate instance mid-publish. That produced a real bug... A broker
> would not have reproduced it.*

Both cannot be true. If behaviour differs by transport, the transport is not an implementation
detail — it is a load-bearing assumption that the module code has been written against.

### The second instance, which is worse because nobody has noticed it

`ReservationFailedHandler` is the compensating action for the scheduling saga. It calls
`ReturnToTriage`, which the aggregate guards:

```csharp
public Result ReturnToTriage(string reason)
{
    if (Status != WorkOrderStatus.Scheduled)
    {
        return Result.Failure(WorkOrderErrors.WrongStatus("un-schedule", Status, WorkOrderStatus.Scheduled));
    }
    ...
}
```

When that guard rejects the transition, the handler does this:

```csharp
if (result.IsFailure)
{
    // Logged, not thrown. A failure here is almost always benign and self-correcting:
    // the order was cancelled, or a duplicate delivery arrived and the first one already
    // did the work.
    logger.LogInformation(
        "Work order {WorkOrderId} was not returned to triage ({Code}). "
        + "Expected when the order was already cancelled or the event was redelivered.",
        e.WorkOrderId, result.Error.Code);
}
```

The comment enumerates two cases and calls the outcome *expected*. **On an in-process bus those
genuinely are the only two cases**, because the publish is synchronous — the reservation failure
comes back before any human could possibly act on the order.

**Under a broker there is a third case, and it is not benign.** The reply is asynchronous and
arbitrarily delayed. A dispatcher sees the order in `Scheduled`, hits "Start", and the order moves
to `InProgress`. The failure event then arrives, `ReturnToTriage` refuses the transition, and the
handler logs *Expected* at Information level.

The resulting state:

| | |
|---|---|
| Work order | `InProgress`, then `Completed` |
| Scheduling | no reservation exists for that technician |
| Billing | invoices the job |
| Operations | a technician worked a job they were never booked for |
| The only trace | one Information log line that says *"Expected"* |

That is a silent business-level divergence, and it is currently impossible **only** because of a
property of the transport the design describes as swappable.

### How this changed the design

Three changes, in increasing order of cost:

1. **The claim came out of the ADR.** The Consequences section now states that swapping the
   transport is a behavioural change requiring every handler to be re-examined, not a
   configuration change. That is the single largest difference between what I would have written
   before this review and what I wrote after.
2. **The log line is wrong and is now a work item.** "Expected" is true for two of three cases. A
   failure arriving when the order is `InProgress` is an anomaly and must be logged as one —
   ideally with the compensation escalating rather than shrugging.
3. **The build plan was reordered.** The outbox and a real broker were originally late items,
   on the reasoning that they are "just infrastructure". They are now days 3 and 4, *before* the
   API and the read models, because every handler written in the meantime would otherwise be
   written against a transport that lies about delivery.

---

## Four more findings

### 1. The saga has no timeout

WorkManagement publishes `WorkOrderScheduledV1` and moves to `Scheduled`. Scheduling replies with
either `TechnicianReservedV1` or `TechnicianReservationFailedV1`.

Nothing handles **neither**. If Scheduling never replies — crash, dropped event, poison message —
the order sits in `Scheduled` indefinitely, having told a customer that somebody is coming.

`SlaSweeper` is the only background service in the solution, and it watches *due dates*, not saga
completion. For a Low-priority order the due date could be days away, so a booking that silently
failed would surface days later as an SLA breach rather than immediately as a stuck saga.

**Verified:** one `BackgroundService` in the solution; no timeout, deadline or stuck-saga handling
anywhere in `src/`.

### 2. "Breach is computed, never stored" has a cost the design does not state

The reasoning given is good: a stored flag is wrong from the moment the deadline passes until the
next sweep, and writing to every open order to keep a boolean honest is a lot of contention to buy
a value a subtraction already gives you.

But computing it from current code means **historical SLA reports change when the code changes**.
Alter the due-date derivation and last quarter's breach report silently rewrites itself. For a
business with contractual SLAs, the report is a record, not a view.

The design already recognises this for the due date — *"derived at triage, once... recalculating
on read would move deadlines already promised"*. The same argument applies to the breach flag at
the moment of breach. It is inconsistent to freeze one and compute the other.

### 3. The most important invariant in Scheduling is currently unenforceable

The design is explicit that an aggregate is a transactional consistency boundary, and that labour
entries live inside `WorkOrder` because *"a work order cannot be completed with no labour logged"*
must be answerable without a query.

Scheduling's equivalent invariant — **do not double-book a technician** — is not enforceable at
all. Known gap 4 says so, and says the fix is "a database constraint, not more C#". There is no
database. So the one rule the module exists to uphold is deferred to a component that does not
exist, while the design argues elsewhere that invariants must not depend on a query.

This is not wrong as a plan. It is under-stated as a risk.

### 4. Billing may not be a bounded context

Billing reacts to one event and prices from minutes and a rate. That is one aggregate and one
rule.

The design's own test for a supporting context is *"necessary, valuable, but the business would not
choose to build it if it could buy it"* — which is an argument for Billing being an **integration
with an accounting system**, not a module. If the real system will eventually post to Xero or
QuickBooks, then `Invoice` is an anti-corruption layer wearing a domain model's clothes, and
modelling it as a peer context now teaches the wrong lesson about where the boundary goes.

Worth a sentence in the design either way, because "we will buy this later" changes what the
contract between it and WorkManagement should look like.

---

## What the review did not find

Stated so the review reads as a review rather than a list of complaints:

- **The aggregate boundary is argued correctly.** Labour in, technician out, invoice out — each
  with a stated invariant and a stated cost. The technician-as-write-lock argument is the
  strongest paragraph in the document.
- **The three async flows each exist for a different reason** — a compensating saga, a decoupled
  critical path, and a reaction to the *absence* of an event. That third one is the kind of thing
  usually missed entirely.
- **The known gaps are real gaps, honestly stated**, not a disclaimer. Four of the five are
  scheduled in the build plan; the fifth is this review's finding 1.
- **The guardrail has been proven to fail.** Most architecture-test suites have never been shown
  to catch anything.
