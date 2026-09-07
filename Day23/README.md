# Day 23 — Bicep IaC

Infrastructure for the **Day 22 piece 2 capstone** (`Dispatch`), described as parameterised Bicep
modules with separate dev and prod parameter files. No portal click-ops.

**Deliverable:** [EXERCISE.md](EXERCISE.md) — main Bicep, one module, both parameter files, and
the what-if output.
**Change-by-change walkthrough:** [update_code.md](update_code.md) — every file, and why.

## Result

| | |
|---|---:|
| `az bicep build` | clean, **0 warnings** |
| what-if, dev | **13 to create, 0 errors** |
| what-if, prod | **13 to create, 0 errors** |
| Modules skipped by what-if | **0** |
| Secrets in the deployment | **0** |

Captured: [`docs/whatif-dev.txt`](docs/whatif-dev.txt) · [`docs/whatif-prod.txt`](docs/whatif-prod.txt)

## Why this pairs with Day 22 piece 2

Every resource closes a gap the capstone recorded against itself:

```
"No persistence. All three stores are dictionaries."          →  modules/sql.bicep
"Replace InProcessIntegrationEventPublisher with a topic."    →  modules/servicebus.bicep
"No metrics export ... observable only if somebody asks."     →  modules/observability.bicep
```

The Service Bus topology is not generic. There is one topic per publishing aggregate, one
subscription per **consuming module** — `scheduling` and `billing` — and a SQL filter on each so a
subscriber only receives the event types it actually handles.

## Layout

```
Day23/
├── infra/
│   ├── main.bicep                 composition root: naming, wiring, outputs
│   ├── main.dev.bicepparam        cheap, disposable, scale-to-zero
│   ├── main.prod.bicepparam       durable, always-warm, zone-redundant
│   └── modules/
│       ├── sql.bicep              Entra-only auth — there is no admin password
│       ├── servicebus.bicep       topic + 2 filtered subscriptions, local auth disabled
│       ├── api.bicep              Container App with a system-assigned identity
│       ├── observability.bicep    Log Analytics + App Insights
│       └── rbac.bicep             role assignments — separate for a reason the compiler enforces
├── scripts/
│   ├── whatif.sh                  read-only plan
│   └── deploy.sh                  plans, confirms, deploys, prints the one manual step
└── docs/
    ├── whatif-dev.txt
    └── whatif-prod.txt
```

## No secrets, and what that means concretely

| Resource | How access works | What is absent |
|---|---|---|
| Azure SQL | `azureADOnlyAuthentication: true` | no `administratorLoginPassword` |
| Service Bus | `disableLocalAuth: true` + RBAC | no SAS connection string |
| Container App | system-assigned managed identity | `secrets: []` — empty, not omitted |

Zero `Microsoft.KeyVault` resources and zero `secureString` parameters. There is nothing to keep
safe because nothing was created.

The connection string is emitted as a plain output on purpose — it ends
`Authentication=Active Directory Default`, which tells the driver to fetch a token from the
platform. It carries no credential, so it is as safe to print as a hostname.

## dev vs prod

Read back out of the two captured plans, not asserted:

| | dev | prod |
|---|---|---|
| SQL SKU | `GP_S_Gen5_1` serverless | `S1` provisioned |
| auto-pause | 60 min | disabled (`-1`) |
| zone redundant | no | **yes** |
| backup retention | 7 days | 35 days |
| Service Bus | `Standard` | `Premium` |
| max delivery count | 3 | 10 |
| message TTL | `P1D` | `P14D` |
| container CPU | 0.25 | 1.0 |
| **min replicas** | **0** | **2** |
| log retention | 30 days | 90 days |
| daily log cap | 1 GB | 10 GB |

Two of those carry most of the weight. **`minReplicas: 0`** is the largest cost saving in dev — an
idle environment runs no containers. **`minReplicas: 2`** in prod is the smallest number that makes
a rolling restart invisible; one replica means every deploy is a total outage for as long as the
replacement takes to start.

## Running it

```bash
cd Day23

./scripts/whatif.sh dev      # read-only. Safe against anything.
./scripts/whatif.sh prod

./scripts/deploy.sh dev      # plans, shows it, refuses without explicit confirmation
```

Both scripts resolve two values from the environment, falling back to discovery:

```bash
export SQL_ADMIN_OBJECT_ID=$(az ad group show --group sql-dispatch-admins --query id -o tsv)
export CONTAINER_APP_ENV_ID=$(az containerapp env list --query '[0].id' -o tsv)
```

Neither is a secret — both are public identifiers — but both are specific to one directory and one
subscription, so committing them would be wrong for anyone else who clones this. `.bicepparam`
reads them with `readEnvironmentVariable`, which a JSON parameter file cannot express.

## What is not done

- **Nothing has been deployed.** Every claim here comes from `what-if`, which validates against ARM
  but creates nothing. The template is proven valid and plannable, not proven to run.
- **Prod shares dev's region and Container Apps environment**, because this subscription permits
  exactly one environment in total. It should not, and [EXERCISE.md](EXERCISE.md#6-honest-gaps)
  explains what breaks as a result.
- **The database grant is a manual step.** `CREATE USER … FROM EXTERNAL PROVIDER` is T-SQL with no
  ARM equivalent. It is emitted as a template output and run by `deploy.sh`.
- **SQL is publicly reachable** behind a firewall rule. A private endpoint is the real answer.
- **No CI.** Day 17's OIDC federated-credential workflow is the pattern to reuse.
