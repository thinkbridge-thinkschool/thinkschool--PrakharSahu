# Day 32 — ship, demo, postmortem

Everything built between Day 1 and Day 31 that can be deployed, deployed to Azure and driven
through a live demonstration.

Two systems ship here, and they are genuinely different things:

| | What it is | Where it came from |
|---|---|---|
| **Quotes** | A three-tier product: Angular client, token broker, modular-monolith API | Days 1–27, the whole first arc |
| **Dispatch** | A domain-driven capstone: three bounded contexts, one process | Days 22p2, 28–31 |

Dispatch had never been deployed anywhere before today. Quotes had, but only the Day 17 version of
it — the one from before background jobs, messaging, the outbox, caching and resilience existed.
This is the first time the current version of either has run in Azure.

---

## Layout

```
apps/
  dispatch/          the Day 31 capstone, unchanged apart from container publishing
  quotes/
    backend/         the Day 27 modular monolith — 23 projects, 5 modules, 1 process
    bff/             the Day 22 token broker (Node)
    frontend/        the Day 22 Angular client
    tests/           the backend's test suites
infra/
  main.bicep         subscription-scoped: creates the group, grants across two others
  modules/           identity, acr-pull, keyvault, sql, apps
scripts/
  lib.sh             shared configuration. No secrets in it.
  build-images.sh    build and push all four images
  deploy.sh          ship it
  demo.sh            drive the live deployment through ten scenes
  teardown.sh        stop paying for it
tools/
  SqlGrant/          creates the managed identity's database user (ARM cannot)
docs/
  demo-transcript.txt   what the demo actually printed
  deployment.txt        what Azure actually contains
```

---

## Running it

```bash
az login
bash scripts/build-images.sh     # ~8 minutes, needs Docker Desktop running
bash scripts/deploy.sh           # ~6 minutes
bash scripts/demo.sh | tee docs/demo-transcript.txt
```

Then, when you are done:

```bash
bash scripts/teardown.sh
```

### What you need

- Azure CLI, signed in, on a subscription where you own the Day 17 app registration
- .NET 10 SDK
- Docker Desktop **running** — the Node and nginx images need it
- Node 20+

### What it costs

Roughly **₹8–12 a day** while it sits idle. Every container app scales to zero and both databases
auto-pause after an hour, so an untouched deployment is paying for storage and almost nothing else.

Three things are borrowed rather than created, and each saves real money:

| Borrowed | From | Saved |
|---|---|---|
| `quotesday17acr22887` | Day 17 | a second Basic registry's fixed monthly charge |
| `thinkschool-env` | Day 17 | nothing — Azure permits only one per region per subscription |
| `sb-quotes-dev-6bi37i` | Day 26 | ~₹28/day, the standing cost of a Standard Service Bus namespace |

`teardown.sh` knows about all three and deletes none of them.

---

## The shape of it

```
browser
   |
   +--> ca-quotes-web        nginx. Static files, and a CSP whose connect-src
   |                         names the broker and nothing else.
   |
   +--> ca-quotes-bff        the broker. Attaches TWO tokens:
   |         |                 Authorization    the user's own JWT, untouched
   |         |                 X-Caller-Token   its managed-identity token
   |         v
   |    ca-quotes-api        refuses /api/* without a valid caller token
   |         |
   |         +--> Azure SQL [quotes]        Authentication=Active Directory Managed Identity
   |         +--> Key Vault                 the JWT signing key, by reference
   |         +--> Service Bus               quote-events, two subscriptions
   |
   +--> ca-dispatch-api      independent. No auth yet, by design.
             |
             +--> Azure SQL [dispatch]
```

One user-assigned managed identity runs all four. No password exists anywhere in this deployment —
not in a template, not in an app setting, not in CI. There are exactly two secrets, both in Key
Vault, both referenced rather than copied.

---

## The deliverable

**[`EXERCISE.md`](EXERCISE.md)** — the live URLs, the demo, and the postmortem.
**[`POSTMORTEM.md`](POSTMORTEM.md)** — one page: what I would do differently, what the hardest bug
taught me, and the one thing I am proudest of.
