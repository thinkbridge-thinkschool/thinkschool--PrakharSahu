# Day 32 — ship, demo, postmortem

> **Exercise:** Paste the live URL, a link to the demo, and the one-page postmortem.

## The live URL

**https://ca-quotes-web.happydesert-51845d93.centralindia.azurecontainerapps.io**

Four applications and two databases, live on Azure:

| | URL |
|---|---|
| **Web** — Angular, nginx | `ca-quotes-web.happydesert-51845d93.centralindia.azurecontainerapps.io` |
| **Broker** — the token broker | `ca-quotes-bff.happydesert-51845d93.centralindia.azurecontainerapps.io` |
| **Quotes API** — Day 27 modular monolith | `ca-quotes-api.…` · refuses direct calls, by design |
| **Dispatch API** — the Day 31 capstone | `ca-dispatch-api.…` · deployed for the first time |

> **Open it once before showing anyone.** Everything scales to zero and both databases auto-pause
> after an hour. The first request of the day pays a container cold start and a database resume
> back to back — 30 to 60 seconds, and it looks exactly like an outage.

## The demo

[`docs/demo-transcript.txt`](docs/demo-transcript.txt) — **22 passed, 0 failed**, driven against
the live deployment by [`scripts/demo.sh`](scripts/demo.sh).

Ten scenes. It shows the refusals as well as the successes, because a system that only ever
demonstrates its happy path has not demonstrated that it has rules.

| # | What it proves | Result |
|---|---|---|
| 0 | Cold start is real and is the price of scale-to-zero | up in 0–1s (warm) |
| 1 | The API refuses a direct call while `/health` answers 200 | `401` |
| 2 | The caller token is not a substitute for a user | `401` |
| 3 | Register, sign in | `201`, `200`, 257-char token |
| 4 | Write a quote, read it back out of Azure SQL | `201` id=6 |
| 5 | The outbox — write and message commit together | `200` |
| 6 | Competing consumers against a real broker | `200` |
| 7 | Background jobs | `200` |
| 8 | Debug levers are **absent** in production, not guarded | `404` |
| 9 | Dispatch: raise → triage → schedule → start → labour → complete → **invoice** | 1 invoice, £255 |
| 10 | CSP `connect-src` names the broker and nothing else | `200` |

Scene 9 closes the money path against a real clock — the window opens three seconds out and the
demo waits for it, because the rule that work cannot start early only means something against real
time. A second order booked into the same slot is **compensated back to Triaged**, which is a saga
completing inside one request with no distributed transaction anywhere.

### Two other pieces of evidence

[`docs/verification.txt`](docs/verification.txt) — **62 assertions, 0 failures**, from
[`scripts/verify.sh`](scripts/verify.sh). Not the demo: this checks the *wiring*. Every role
assignment, every connection string, every pointer between tiers, and the Angular bundle fetched
from the live site and confirmed to call `/api/v1`.

[`docs/test-results.txt`](docs/test-results.txt) — 76 Quotes tests and 73 Dispatch tests, 0 failed,
0 skipped. The three database-backed Dispatch suites did not run here and the file says so.

---

## The one-page postmortem

**[`POSTMORTEM.md`](POSTMORTEM.md)** — what I would do differently, what the hardest bug taught me,
and the one thing I am proudest of.

---

## What shipping found

Nine defects, none of which any test could have caught, because every one of them was a binding
between two things that were each individually correct.

| # | What broke | Why no test saw it |
|---|---|---|
| 1 | Angular client asked `/api/…`; the API serves `/api/v1/…` | Client and broker predate Day 27's versioning. Nothing runs them together off Azure. |
| 2 | Cache and circuit-breaker endpoints missing in production | Day 27 made them Development-only. Correct, and it means the resilience demo cannot run against prod. |
| 3 | Quotes migrations were **SQLite-shaped** — `TEXT`, `INTEGER` | Replayed on SQL Server they create a schema successfully and create the wrong one. Fixed with a per-provider migrations assembly. |
| 4 | `dotnet run --nologo` shifted every argument right | `dotnet run` forwards flags it does not recognise to the app. `SqlGrant` read the identity's *name* where it wanted its *client id*. |
| 5 | The build script aborted when already correct | It read "text did not change" as "pattern not found". A rebuild must be a no-op, not a failure. |
| 6 | A SAS key for a namespace with **SAS disabled** | Day 26 set `disableLocalAuth: true` on purpose. The credential was stored perfectly and was worth nothing. |
| 7 | **`BeginTransactionAsync` under a retrying execution strategy** | Written Day 20 against SQLite, which has no retry strategy. Threw on the first line against Azure SQL. |
| 8 | The JWT signing key rotated on **every deploy** | Owner on a subscription does not grant access to a vault's *contents*. The read failed, the script minted a new key, and reported success. |
| 9 | An orphaned `servicebus-connection` secret | Bicep does not delete child resources it stops declaring. A dead credential nothing would ever have noticed. |

Three of those — 6, 7 and 8 — are worth dwelling on, because all three **reported success**.

The SAS key was fetched, stored and mounted correctly. The signing key was generated and saved
correctly. Neither step failed; both were pointed at the wrong thing. Number 8 is the worst of the
set: it had a symptom (everyone quietly logged out after each release) that nobody would connect to
a deployment for a long time.

### Two fixes made the design better

**Service Bus.** The fix for #6 was not a better hiding place for the credential — it was to stop
having one. The application already supported a namespace plus a `TokenCredential`, so it now gets
a token from the platform exactly as the SQL driver does. The vault went from two secrets to one.
The grant is **Data Sender + Data Receiver**, not `Data Owner`: this API sends, receives and peeks
dead letters, and has no business creating or deleting topics.

**The architecture tests caught me.** Adding `QuotesApi.Persistence.SqlServer` failed two of them —
*"does not fit the naming scheme, so no boundary rule applies to it"*. I did not widen the rules. I
added a category with a **stricter** constraint than Infrastructure projects get: a migrations
assembly may reference the shared database and **nothing else at all**. Otherwise "it's a migrations
project" becomes a way to reach anything from anywhere. 12 rules became 13.

---

## The deployment

```
browser
   |
   +--> ca-quotes-web       nginx. CSP connect-src names the broker and nothing else.
   |
   +--> ca-quotes-bff       attaches TWO tokens:
   |         |                Authorization    the user's own JWT, untouched
   |         |                X-Caller-Token   its managed-identity token
   |         v
   |    ca-quotes-api       refuses /api/* without a valid caller token
   |         |
   |         +--> Azure SQL [quotes]     Authentication=Active Directory Managed Identity
   |         +--> Key Vault              the JWT signing key, by reference
   |         +--> Service Bus            quote-events, by token — no SAS key
   |
   +--> ca-dispatch-api     independent. No auth yet, by design.
             |
             +--> Azure SQL [dispatch]
```

**One password exists in this deployment: none.** Not in a template, not in an app setting, not in
CI. One user-assigned managed identity runs all four applications, and there is exactly one secret
in Key Vault — the JWT signing key, referenced rather than copied.

Keeping the two tokens in **separate headers** is the whole design. Putting the managed-identity
token in `Authorization` is more conventional, was tried first, and collapses the two identities
into one — so the API's ownership checks read `sub` from the managed identity and silently reassign
every quote to it.

### What it costs, and the tradeoff taken

**~₹10–12/day.** Everything scales to zero; both databases auto-pause after 60 minutes.

A floor of one replica would remove the cold start for roughly ₹57/app/day — more, for one app,
than everything else in this subscription costs together. Not done, on purpose. It is the first
thing the postmortem says it would change.

Three things are borrowed rather than created, and `teardown.sh` deletes none of them:

| Borrowed | From | Saved |
|---|---|---|
| `quotesday17acr22887` | Day 17 | a second registry's fixed monthly charge |
| `thinkschool-env` | Day 17 | nothing — Azure permits one per region per subscription |
| `sb-quotes-dev-6bi37i` | Day 26 | ~₹28/day, and it already had the `quote-events` topic |
| `appi-quotes-dev-6bi37i` | Day 26 | one system's traces belong in one place |

---

## Running it

```bash
az login
bash scripts/build-images.sh    # ~8 min, needs Docker Desktop
bash scripts/deploy.sh          # ~6 min
bash scripts/verify.sh          # 62 assertions on the wiring
bash scripts/demo.sh            # 10 scenes against the live URLs
bash scripts/teardown.sh        # when you are done
```

ACR Tasks are blocked on this subscription (`TasksOperationsNotAllowed`), so images are built
locally: the .NET images through the SDK's own container support with no daemon, the Node and nginx
images through Docker.

## Honest gaps

- **No authentication on Dispatch.** Day 31 said so and left it for build-plan day 8. Adding a
  scheme before the roles exist is security theatre.
- **The CI workflow still has not run.** `dispatch-ci.yml` is not on the default branch.
- **One identity for four applications.** Four would let the frontend hold nothing and each API
  reach only its own database. At four co-deployed apps in one trust boundary, one is an acceptable
  trade for a deployment somebody can hold in their head. The day one of them is operated by a
  different person, it stops being acceptable.
- **Two package advisories** (`Microsoft.OpenApi`, `SQLitePCLRaw`). Neither is reachable in
  production — OpenAPI is Development-only and the SQLite native library never loads when the
  connection string selects SQL Server — but both should be bumped.
