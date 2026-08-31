# Day 18 — Background jobs

Slow work moved off the request thread: a `BackgroundService` draining a `Channel<T>` queue,
contrasted with `IHostedService` and Hangfire, with graceful shutdown driven by the
cancellation token.

**The deliverable is [EXERCISE.md](EXERCISE.md)** — the worker, how it shuts down cleanly, and
the one-line Hangfire answer.

## The measurement that matters

```
POST /api/jobs  →  HTTP 202 in 205ms   for a job that takes 6000ms
```

Response time is independent of how long the work takes. That is the whole point.

## Layout

Same shape as Day 17, plus the jobs pipeline and its tests.

```
Day18/
├── EXERCISE.md              the deliverable
├── backend/                 .NET 10 Quotes API + background jobs
│   ├── Models/Job.cs            job + lifecycle states
│   ├── Jobs/
│   │   ├── JobQueue.cs          IJobQueue + bounded Channel<T> implementation
│   │   ├── JobStore.cs          in-memory status + per-job cancellation registry
│   │   ├── IJobHandler.cs       the handler contract (observe your token)
│   │   ├── JobProcessor.cs      ◀ the BackgroundService
│   │   └── Handlers/            quote-report (real, DB-backed) · simulate (for demos)
│   ├── Hosted/
│   │   └── JobPipelineDiagnostics.cs   ◀ the IHostedService, for contrast
│   ├── Endpoints/JobEndpoints.cs
│   └── Extensions/BackgroundJobExtensions.cs   DI + shutdown timeout
├── tests/QuotesApi.Jobs.Tests/  8 tests, including graceful shutdown
├── frontend/ · bff/ · scripts/  carried over from Day 17
└── scripts/smoke-jobs.sh        end-to-end HTTP check
```

## How the pieces fit

```
POST /api/jobs ──► IJobQueue (bounded Channel<T>) ──► JobProcessor : BackgroundService
      │                                                     │  one DI scope per job
      │ 202 + Location                                      ▼
      ▼                                                 IJobHandler
GET /api/jobs/{id}  ◀──── IJobStore ◀──── status, progress, result
```

`DELETE /api/jobs/{id}` signals that job's `CancellationTokenSource`; shutdown signals every
job's, after a grace period.

## Endpoints

| Method | Route | Auth | Behaviour |
|---|---|---|---|
| `POST` | `/api/jobs` | required | `202` + `Location`. `400` unknown type · `503` shutting down |
| `GET` | `/api/jobs/{id}` | anonymous | status, progress, result/error, queue latency, duration |
| `GET` | `/api/jobs` | anonymous | recent history plus live `queueDepth` |
| `DELETE` | `/api/jobs/{id}` | required | `202` — a cancellation *request*. `409` if already finished or not yet started |

`POST` and `DELETE` require a token because an anonymous caller who can enqueue expensive work
has a denial-of-service primitive. The bounded queue caps the blast radius; the token removes
the anonymous half of it.

## Job types

| Type | What it does |
|---|---|
| `quote-report` | Reads every quote through a scoped `IQuoteRepository` and summarises by author. The realistic slow job. |
| `simulate` | Duration and outcome from the payload: `{"durationMs":6000,"shouldFail":false}`. Produces any lifecycle state on demand. |

## Running it

```bash
# Unit tests, including graceful shutdown — deterministic, no host needed
cd Day18/tests/QuotesApi.Jobs.Tests && dotnet test        # 8 passed

# End-to-end over HTTP: enqueue, poll, fail, cancel, report
cd Day18 && bash scripts/smoke-jobs.sh                     # 13 passed
```

Locally, by hand:

```bash
cd backend
Jwt__Key="$(openssl rand -base64 48)" dotnet run            # :5267

TOKEN=$(curl -s -X POST localhost:5267/api/auth/register \
  -H 'Content-Type: application/json' \
  -d '{"email":"me@example.invalid","password":"Passw0rd-long-enough"}' | jq -r .accessToken)

curl -i -X POST localhost:5267/api/jobs -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"type":"quote-report"}'
# → 202 Accepted, Location: /api/jobs/<id>

curl -s localhost:5267/api/jobs/<id> | jq
```

Then press **Ctrl+C** while a job is running and watch the shutdown sequence in the logs:
queue closed → grace period announced → job finishes or is cancelled → processor stopped.

## Configuration

```json
"BackgroundJobs": {
  "Capacity": 100,
  "ShutdownGrace": "00:00:10"
}
```

`HostOptions.ShutdownTimeout` is derived as `ShutdownGrace + 10s` rather than configured
separately — the framework default is 5 seconds, which is *shorter* than the grace period and
would let the host kill the process mid-job. See
[EXERCISE.md](EXERCISE.md#the-bound-on-all-of-it).

## Relationship to Day 17

`frontend/`, `bff/` and the deployment scripts are carried over unchanged; the Day 17
managed-identity work still applies, and `/api/jobs` sits behind the same caller-token
enforcement as the rest of `/api/*`. Nothing here has been deployed — Day 18 is a local
exercise.
