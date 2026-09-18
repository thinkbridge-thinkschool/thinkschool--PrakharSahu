# When this codebase moves from signals to a store

Signals and a service are the default. A store library is a real cost — indirection, ceremony,
a second vocabulary to teach — so it has to be paid for by a problem we actually have.

## The rule

Stay on **signals + an `@Injectable` service** until **two or more** of these are true. One
alone is usually a smell you can fix without a library.

| # | Threshold | Why this one |
|---|---|---|
| 1 | **Three or more features _write_ the same slice.** Readers do not count. | Two writers can agree by convention. Three is where "who changed this?" stops being answerable by reading, and an explicit action log starts paying for itself. |
| 2 | **The slice outlives the route that owns it**, and correctness depends on that. | A root-provided service already survives navigation. This threshold is about *needing* it to — a draft that must survive a detour, a cart, an upload queue. |
| 3 | **You need to replay what happened** to diagnose a bug you cannot reproduce. | Devtools time-travel is the one thing signals genuinely cannot give you. If support tickets say "it just went blank", that is the trigger. |
| 4 | **Async work needs coordinating across features** — cancellation, sequencing, retry-on-a-chain. | One `async` method per command is fine. A dependency graph of them is what `Effects` exist for. |
| 5 | **More than ~1,000 entities** where you need normalized `byId` lookups and memoised selectors to keep rendering cheap. | Below that, `array.filter()` in a `computed` is faster than the entity adapter it would replace. |
| 6 | **More than one team edits the state layer** and needs an enforced contract. | Here the ceremony *is* the value: a shape nobody can quietly bypass. |

## Take the intermediate step first

If two thresholds are met, reach for **`@ngrx/signals` (SignalStore)** before full
NgRx `Store` + `Effects`. It buys `withEntities`, `withMethods`, devtools and a shared
convention while staying signal-native, so no component changes. Full NgRx is for thresholds
**3, 4 and 6** — the ones that actually want actions, reducers and an effect graph.

## Applied to this feature, today

`QuotesStore` holds the quotes list and the optimistic-delete layer.

| # | Threshold | Met? |
|---|---|---|
| 1 | ≥3 writers | **No.** One feature writes it. Day 14's create form and this list are two, and they do not overlap. |
| 2 | Must outlive the route | **No.** It is root-provided so a back-navigation is cheap, but nothing breaks if it is not. |
| 3 | Need replay | **No.** Four writable signals; the whole state prints in one `console.log`. |
| 4 | Cross-feature async | **No.** `load()` and `remove()` are independent, and both are ordinary `async` methods. |
| 5 | >1,000 entities | **No.** `GET /api/quotes` returns a bare array with no paging — the whole table, currently 5 rows. |
| 6 | Multiple teams | **No.** |

**Verdict: signals.** Nothing here is waiting on a library.

## What would actually flip it

The realistic trigger is **threshold 1 plus threshold 5, arriving together**. Concretely: if
`POST /api/quotes` (Day 14) and `PUT /api/quotes/{id}/author` both start writing this same
collection alongside the list — three writers — *and* the server grows real paging so the
client holds pages rather than the whole table. At that point `withEntities` is doing work I
would otherwise hand-roll badly, and I would move to `@ngrx/signals` rather than full NgRx,
because none of 3, 4 or 6 would be true even then.
