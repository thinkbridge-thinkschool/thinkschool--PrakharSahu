# Day 28 — Design review + ADR

A critique of the `Dispatch` capstone design, one ADR for the decision everything else hangs off,
and a ten-day plan to build it.

**Deliverable:** [EXERCISE.md](EXERCISE.md)

## Layout

```
Day28/
├── EXERCISE.md                                     the deliverable: critique, ADR, plan
├── adr/
│   └── 0001-modular-monolith-over-microservices.md the decision, in full
└── docs/
    ├── design-critique.md                          5 findings, each checked against the code
    └── build-plan.md                               10 days, each with an exit condition
```

## What the review found

The design claims the in-process event bus can be swapped for a broker "and no module changes".
It cannot. Two handlers are correct today only because delivery happens to be synchronous, and
one of them — the compensating action for the scheduling saga — would silently allow a technician
to work a job they were never reserved for, logging it at Information level as *"Expected"*.

That finding moved the transport from a late "just plumbing" item to **day 4** of the build plan,
ahead of the API.

## On provenance

The critique was produced by Claude at my request and is labelled as such throughout, not as
mentor or peer feedback. The exercise asks how a critique *changed* the design; inventing a
mentor quote would have made the ADR dishonest at its foundation.
