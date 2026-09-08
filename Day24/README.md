# Day 24 — Deployment Stacks + azd

Day 23's Bicep, actually deployed. Driven by the **azd CLI**, managed as an **Azure Deployment
Stack** so teardown is exact and drift is refused rather than merely noticed. dev first, then
promoted to prod.

**Deliverable:** [EXERCISE.md](EXERCISE.md) — the azd config, deploy output for both
environments, and the one line on what stacks give you.
**Change-by-change walkthrough:** [update_code.md](update_code.md) — every file, and why.

## Result

| | |
|---|---:|
| `azd provision` — dev | **succeeded**, 5m10s |
| `azd provision` — prod | **succeeded**, 3m24s |
| `azd down` — prod | **succeeded**, 8m46s |
| Resources managed per stack | **16** |
| Orphans after teardown | **0** |
| Drift proof | **5 passed, 0 failed** |
| Bugs deploying found that planning could not | **3** |

## What changed from Day 23

Day 23 produced a template that planned cleanly and was never deployed. Deploying it needed
exactly one structural change and found three bugs.

```
Day23/infra/main.bicep     ->  Day24/infra/resources.bicep     (unchanged design, now a module)
                               Day24/infra/main.bicep          (NEW — subscription scope)

main.dev.bicepparam        ->  infra/profiles/dev.json
main.prod.bicepparam       ->  infra/profiles/prod.json
                               infra/main.bicepparam           (NEW — selects one profile)

scripts/whatif.sh          ->  azd provision --preview  (unsupported with stacks)
scripts/deploy.sh          ->  azure.yaml + azd provision
```

The entry point moves up to **subscription scope** so the resource group becomes a managed
resource rather than a prerequisite. That is what makes teardown complete: Day 23 left
`rg-dispatch-dev` and `rg-dispatch-prod` behind because a script created them and no template
owned them.

## Layout

```
Day24/
├── azure.yaml                     the azd manifest — stack config, hooks
├── infra/
│   ├── main.bicep                 SUBSCRIPTION scope: declares the resource group
│   ├── main.bicepparam            ONE parameter file; selects a profile by azd env name
│   ├── resources.bicep            Day 23's composition root, now a module
│   ├── profiles/
│   │   ├── dev.json               cheap, disposable, scale-to-zero
│   │   └── prod.json              durable, always-warm
│   └── modules/                   unchanged from Day 23 except two fixes
│       ├── sql.bicep              Entra-only auth — there is no admin password
│       ├── servicebus.bicep       topic + 2 filtered subscriptions, local auth disabled
│       ├── api.bicep              Container App with a system-assigned identity
│       ├── observability.bicep    Log Analytics + App Insights
│       └── rbac.bicep             sender + receiver, nothing broader
├── scripts/
│   ├── env-setup.sh               create an azd env and resolve its two identifiers
│   ├── promote.sh                 gate on dev, print the diff, provision prod
│   ├── drift-proof.sh             prove the stack properties — 5 assertions
│   └── postprovision.sh           the T-SQL grant no template can express
└── docs/                          captured output from every run above
```

## Running it

One-time, per machine:

```bash
azd config set alpha.deployment.stacks on   # deployment stacks are still alpha in azd 1.31
azd config set auth.useAzCliAuth true       # reuse the `az login` session instead of a second one
```

Then:

```bash
cd Day24

./scripts/env-setup.sh dev        # creates the azd env, resolves the admin group + CA environment
azd provision --no-prompt         # deploys dev

./scripts/drift-proof.sh dev      # proves the inventory, the deny, and the revert
./scripts/promote.sh              # gates on dev, shows the diff, deploys prod

azd down --force                  # tears down whichever env is selected
```

`--force` and **not** `--purge`. `--purge` issues a direct delete that the stack's own deny
assignment blocks — see [EXERCISE.md](EXERCISE.md#azd-down---purge-is-incompatible-with-deny-settings).

## dev vs prod

The entire difference, and it lives in two files:

```bash
diff infra/profiles/dev.json infra/profiles/prod.json
```

| | dev | prod |
|---|---|---|
| SQL SKU | `GP_S_Gen5_1` serverless | `GP_Gen5_2` provisioned |
| auto-pause | 60 min | none |
| backup retention | 7 days | 35 days |
| Service Bus | `Standard` | **`Premium`** |
| max delivery count | 3 | 10 |
| message TTL | `P1D` | `P14D` |
| container CPU | 0.25 | 1.0 |
| **min replicas** | **0** | **2** |
| log retention | 30 days | 90 days |
| daily log cap | 1 GB | 10 GB |

Two carry most of the weight. `minReplicas: 0` is the largest cost saving in dev — an idle
environment runs no containers. `minReplicas: 2` in prod is the smallest number that makes a
rolling restart invisible.

Every prod value above was read back **from Azure** after the deployment, not from the file that
asked for it: [`docs/prod-verification.txt`](docs/prod-verification.txt).

## No secrets, unchanged from Day 23

| Resource | How access works | What is absent |
|---|---|---|
| Azure SQL | `azureADOnlyAuthentication: true` | no `administratorLoginPassword` |
| Service Bus | `disableLocalAuth: true` + RBAC | no SAS connection string |
| Container App | system-assigned managed identity | `secrets: []` — empty, not omitted |

Zero `Microsoft.KeyVault` resources and zero `secureString` parameters. The connection string is a
plain output on purpose — it ends `Authentication=Active Directory Default`, so it names an auth
method rather than carrying a credential.

## What is not done

- **Prod no longer exists.** It was deployed, verified, and torn down so Premium Service Bus and a
  provisioned database stop billing a student subscription. Every prod figure here came from the
  live environment.
- **The postprovision hook does not fire on `azd provision`** with stacks enabled. Run
  `azd hooks run postprovision` after a provision, or install `sqlcmd` first.
- **The database grant was never applied** — `sqlcmd` is not on this machine, so the API can reach
  SQL and not read from it.
- **No application code is deployed.** No `services:` block; the Container App runs the public
  quickstart image. Wiring in Day 22 piece 2 needs a registry this template does not create.
- **Prod could not be zone-redundant.** The region and the SKU both support it; the student
  subscription does not.
- **SQL is publicly reachable** behind a firewall rule. A private endpoint is the real answer.
- **No CI.** Day 17's OIDC workflow is the pattern to reuse.
