# Day 31 — Polish: tests, perf, security

> **Exercise:** Paste the CI run (green), the test coverage at each layer, and the hot-path p99
> before/after polish.

| | Day 30 | Day 31 |
|---|---:|---:|
| Tests | 76 | **103** |
| Test projects | 4 | **6** |
| Layers of the pyramid | 2 | **4** |
| Hot-path p99 | not measured | **4.02 ms** (from 5.14) |
| CI | pointed at a Week 1 exercise | **a gate that has refused a build** |

Evidence: [`docs/test-results.txt`](docs/test-results.txt) ·
[`docs/coverage-by-layer.txt`](docs/coverage-by-layer.txt) ·
[`docs/perf-before.txt`](docs/perf-before.txt) · [`docs/perf-after.txt`](docs/perf-after.txt) ·
[`docs/security-recheck.txt`](docs/security-recheck.txt) ·
[`docs/ci-gate-proof.txt`](docs/ci-gate-proof.txt)

---

## 1. The pyramid

Day 30 had unit tests and one concurrency test. The middle was missing entirely: no
`WebApplicationFactory`, no `Mvc.Testing` reference, nothing that had ever sent an HTTP request.

| Layer | Project | Tests | What only it can catch |
|---|---|---:|---|
| **Unit** | `WorkManagement.Domain.Tests` | 44 | Aggregate rules, in isolation, with a fake clock |
| **Unit** | `WorkManagement.Application.Tests` | 17 | Cross-module flows against ports, with fakes |
| **Architecture** | `ArchitectureTests` | 12 | A forbidden project reference, before anyone uses it |
| **Integration** | `Api.IntegrationTests` | **26** | **New.** Routing, binding, JSON, status codes, headers — the real pipeline, real database |
| **Integration** | `Scheduling.Concurrency.Tests` | 3 | The booking race, against a real SQL Server |
| **E2E** | `E2E.Tests` | **1** | **New.** The real binary, a real socket, the full money path |

**103 tests, 0 skipped.**

### What the new layer found immediately

Two status codes were wrong, and no unit test could have seen them because no unit test sees a
status code:

| Code | Was | Now | Why |
|---|---|---|---|
| `work_order.no_labour` | 400 | **409** | The request is fine; the order needs labour logged first |
| `work_order.window_not_open` | 400 | **409** | The request is fine; it is too early |

A 400 tells a caller to fix their payload. There was nothing to fix. The mapping is now explicit
for every code and the default **throws** — an unclassified error is loud in a test run rather
than a plausible-looking 400 in production.

That decision paid for itself within the hour: the security audit sent a 5 MB body and got a
**500**, because `address.incomplete` and `address.line_too_long` had never been classified.

### One E2E, deliberately

`WebApplicationFactory` is in-process — no socket, no Kestrel, `Main` never runs. The E2E starts
the real binary as a separate process on a real port and drives raise → triage → schedule →
start → labour → complete → invoice. It waits three real seconds for a scheduled window to open,
because that rule only means anything against a real clock.

There is one because it is slow, and slow suites stop being run. Everything narrower lives below
it.

---

## 2. Perf: the hot path

`GET /api/work-orders/{id}` — the read a dispatcher refreshes and a technician's app polls.

**Median of 3 runs × 2,000 requests, concurrency 8, warm process, 8 labour rows.**

| | before | after | |
|---|---:|---:|---|
| p50 | 2.67 ms | 2.29 ms | −14% |
| p95 | 3.79 ms | 3.37 ms | −11% |
| **p99** | **5.14 ms** | **4.02 ms** | **−22%** |
| throughput | ~2,850 rps | ~3,350 rps | +18% |

Two changes:

**`AsNoTracking` on the read path.** Every read built change-tracking entries for the aggregate
root and every owned labour row, then kept them alive — pure waste on a query that serialises the
result and forgets it. It is a **second method** (`GetForReadAsync`), not a flag: making
`GetAsync` untracked would have been one word and would have silently broken every write.

**Pooled DbContexts.** A `DbContext` is cheap to use and not cheap to create — each one builds an
internal service provider and resolves its model. That cost was paid on every request, before any
SQL was sent.

### The measurement was wrong before it was right

The first attempt reported **p99 6.82 ms before, 8.84 ms after** — the change looked like a 29%
regression. Both were single runs on a cold process, where the p99 is one request whose cost was
set by a JIT pause.

Repeating three times and taking the median turned that into a 22% improvement. The harness now
does this by construction, and the floor is documented: `/health`, which touches no database, is
0.52 ms — so roughly 1.8 ms of the remaining 2.29 ms is the database round trip.

---

## 3. Security re-check

| Control | Before | After |
|---|---|---|
| `Server: Kestrel` banner | present | removed |
| `X-Content-Type-Options`, `X-Frame-Options`, CSP, `Referrer-Policy`, CORP | all missing | all present |
| Headers on error responses | missing | present |
| 5,000-char summary (column is 500) | **HTTP 500** | HTTP 400 |
| 5,000-char address line | **HTTP 500** | HTTP 400 |
| Max request body | 30 MB (Kestrel default) | 64 KB |
| Rate limiting | none | 300/min per caller |

**The finding worth keeping:** the `Summary` column is `nvarchar(500)` and the domain never
checked length. Anything longer passed every domain rule, reached SQL Server, and failed there —
so the caller was told the server had broken when their request was simply too big. Validation
was weaker than storage, which is both a wrong status code and a cheap way to make the server do
expensive work on unbounded input.

Every row above is asserted by a test in `SecurityTests.cs`, so it runs on every push. Day 27
proved the same controls with a shell script run by hand, and run by hand means run once.

**Still open, deliberately:** authentication and authorization. Dispatch has no notion of a
caller, and adding a scheme before the roles exist is security theatre. Build-plan day 8.

---

## 4. The CI gate

`.github/workflows/dispatch-ci.yml`. The repository's existing `ci.yml` still points at a Week 1
exercise; this one builds and tests the capstone.

- **SQL Server 2022 as a service container**, not Azure SQL — the whole suite needs a real
  database, and a throwaway one on the runner is faster, costs nothing, needs no credentials in
  CI, and cannot be left running by a job that failed.
- `-warnaserror`, Release configuration.
- Coverage collected and uploaded; threshold 60%.
- **A gate that fails the build if any test was skipped.**

That last one matters because every database-backed test here skips when its connection string is
absent. Without the gate, a runner whose service container failed to start reports a confident
green build having executed 73 of 103 tests.

### The gate was wrong twice

| Attempt | Why it reported 0 skipped for a run that skipped 30 |
|---|---|
| `grep 'outcome="NotExecuted"'` | Every project wrote to the **same** `LogFileName`, so five of six result files were overwritten before the gate ran |
| Reading the `notExecuted` counter | A `SkippableFact` skip does not set it |

The signal that works is **`total − executed`**, summed across every `.trx`. Proven in both
directions in [`docs/ci-gate-proof.txt`](docs/ci-gate-proof.txt).

---

## Coverage by layer

| Layer | Line coverage |
|---|---:|
| Application | 89.4% |
| Infrastructure | 90.0% |
| Host / API | 87.9% |
| Domain | 75.3% |
| SharedKernel | 71.7% |
| Contracts | 68.0% |
| **Overall** | **80.6%** |

Full breakdown in [`docs/coverage-by-layer.txt`](docs/coverage-by-layer.txt).
`Billing.Contracts` at 33% is the honest low point — a record whose factory only the integration
tests construct.

---

## Honest gaps

- **The CI run itself has not executed.** Every step was run locally in the exact configuration
  the workflow uses — Release, `-warnaserror`, `--no-build`, against a SQL Server 2022 container,
  103 passed, 0 skipped — but a green GitHub Actions run needs a push, and nothing here has been
  committed.
- **The perf numbers are from a laptop against a local container.** Useful as a before/after on
  identical hardware; not a production latency budget. Dispatch is not deployed — build-plan
  day 9.
- **No authentication.** Above, and deliberate.
- **The E2E waits three real seconds.** It is the only test that sleeps, and it does so because
  the rule it is proving only exists against a real clock.
