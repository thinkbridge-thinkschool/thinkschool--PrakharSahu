# Threat model — STRIDE-lite

Scope: the Quotes API and its data tier as deployed by Days 23–26. One page, because a threat
model nobody rereads is a document, not a control.

## The system, and where trust changes

```
  internet ──┬──> [1] API (quotes-api)        ──┬──> [3] Azure SQL
             │        JWT bearer auth           ├──> [3] Service Bus topic
             │                                  └──> [3] Key Vault
             └──> [2] worker (quotes-worker)  ──┘
                      no ingress
```

Three boundaries, and each is crossed by a different kind of caller:

| # | boundary | who crosses it | authenticated by |
|---|---|---|---|
| 1 | internet → API | anyone | JWT bearer, issued by `/api/auth/login` |
| 2 | API → worker | nobody directly — via an outbox row and a topic | — |
| 3 | app → data tier | the workload identity | Entra token (managed identity) |

**Assets worth an attacker's time**, in order: the JWT signing key (mints any identity), the user
table's password hashes, quote data, and the telemetry stream (which is write-only, so of limited
value).

## STRIDE

`S`poofing · `T`ampering · `R`epudiation · `I`nformation disclosure · `D`enial of service ·
`E`levation of privilege.

| # | Threat | S T R I D E | Before today | Status |
|---|---|---|---|---|
| T1 | **Unauthenticated control endpoints.** `/api/cache` and `/upstream` had no `RequireAuthorization` at all, and expose `POST /mode`, `POST /reset`, `POST /breaker/{action}` and fault injection. | · · · · D E | anyone could disable caching or trip the circuit breaker | **fixed** |
| T2 | **Authorization was opt-in.** No fallback policy, so a new endpoint that forgets `RequireAuthorization()` is public. T1 is a symptom; this is the cause. | · · · · · E | one omission = a public endpoint | **fixed** |
| T3 | **Credential stuffing on `/api/auth/login`.** No rate limit. BCrypt makes each guess costly for the server too, so it is also a DoS vector. | S · · · D · | unbounded attempts | **fixed** |
| T4 | **Unbounded request bodies.** Kestrel's 30 MB default applied to every endpoint, including login. | · · · · D · | 30 MB of JSON per request | **fixed** |
| T5 | **Missing security headers.** No HSTS, CSP, `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`. | · T · I · · | MIME sniffing, clickjacking, referrer leakage | **fixed** |
| T6 | **Server banner leaks the stack.** `Server: Kestrel` tells a scanner what to target. | · · · I · · | version fingerprinting | **fixed** |
| T7 | **Data tier reachable from the internet.** SQL and Service Bus had public endpoints behind a firewall rule and Entra auth. A stolen token is usable from anywhere. | S · · I · E | network reachable worldwide | **fixed** |
| T8 | **No API versioning.** A breaking change has nowhere to go, so it lands on existing clients. | · T · · · · | implicit v1 forever | **fixed** |
| T9 | **Repudiation on writes.** Quotes record a `userId`, but nothing immutable ties a change to a principal — logs are the only record and they are not tamper-evident. | · · R · · · | best-effort | **accepted** |
| T10 | **JWT has no revocation.** A stolen access token is valid for its full 2-hour life; nothing can cut it short. | S · · · · E | 2-hour exposure | **accepted** |
| T11 | **Password hashes are the crown jewel.** BCrypt at default work factor. Adequate now, and a number that must be raised as hardware improves. | · · · I · · | BCrypt default | **accepted** |

## The three that were not fixed, and why

**T9 — repudiation.** Genuine non-repudiation needs an append-only audit log the application
cannot rewrite, which means a second store with different credentials. That is a day of work and
a running cost for a system whose disputed-change risk is currently zero. Recorded, not built.

**T10 — token revocation.** A deny-list needs a shared store checked on every request, which
converts a stateless token into a stateful one and adds a dependency to the hot path. The cheaper
mitigation already exists — the access token is short-lived and the refresh cookie can be
invalidated — so the exposure is bounded at two hours rather than eliminated.

**T11 — BCrypt work factor.** The default is fine today. The real fix is not a bigger number but
a rehash-on-login policy so the factor can be raised without a migration; noted for the backlog.

## What the threat model caught that the scanner could not

T1, T2 and T7 are **authorization and network-topology** failures. ZAP's baseline scan is passive
— it spiders and inspects traffic — so it found none of them, and would not have found them in an
active scan either without credentials and a notion of which endpoints *should* be privileged.

Conversely T5 and T6 are exactly what a scanner is good at: they are invisible in code review
because they are about what the server does **not** say.

Neither technique substitutes for the other, which is the reason both are in today.
