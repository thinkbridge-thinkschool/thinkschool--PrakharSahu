# Day 24 — Deployment Stacks + azd

Deploy the Day 22 piece 2 capstone (`Dispatch`) with Azure Deployment Stacks, driven by the azd
CLI. Deploy to dev, then promote to prod.

## The one line

> A plain deployment applies a template and forgets what it made; a **deployment stack keeps a
> server-side inventory of the resources it owns**, which is the single fact that makes exact
> teardown, drift correction, and delete-protection possible at all.

Everything below is a consequence of that one difference.

| | plain deployment (Day 23) | deployment stack (Day 24) |
|---|---|---|
| Knows which resources it created | no | **yes — 16, enumerable** |
| Remove a resource from the template | orphan, still billing | **deleted** |
| Delete a resource in the portal | succeeds silently | **refused by ARM** |
| Tear down the environment | delete the group and hope | **one command, nothing left** |
| Resource group lifecycle | `az group create` in a script, owned by nothing | **managed like any other resource** |

## Result

| | |
|---|---:|
| `azd provision` — dev | **succeeded**, 5m10s |
| `azd provision` — prod | **succeeded**, 3m24s |
| `azd down` — prod | **succeeded**, 8m46s |
| Resources managed per stack | **16** |
| Orphans left after teardown | **0** |
| Drift proof | **5 passed, 0 failed** |
| Bugs found by deploying that planning could not find | **3** |

Captured: [`docs/provision-dev.txt`](docs/provision-dev.txt) ·
[`docs/provision-prod.txt`](docs/provision-prod.txt) ·
[`docs/drift-proof-dev.txt`](docs/drift-proof-dev.txt) ·
[`docs/teardown-prod.txt`](docs/teardown-prod.txt) ·
[`docs/prod-verification.txt`](docs/prod-verification.txt)

---

## 1. The azd config

### `azure.yaml`

The whole project manifest. What matters is what it no longer has to say: no resource group
name, no `az group create`, no `--parameters main.dev.bicepparam`, no timestamped deployment
name. azd derives all of it from the selected environment.

```yaml
name: dispatch
metadata:
  template: dispatch-day24@1.0.0

infra:
  provider: bicep
  path: infra
  module: main

  deploymentStacks:
    actionOnUnmanage:
      resources: delete
      resourceGroups: delete
      managementGroups: detach

    denySettings:
      mode: denyDelete
      applyToChildScopes: false
      excludedPrincipals: []
      excludedActions: []

hooks:
  postprovision:
    shell: sh
    run: ./scripts/postprovision.sh
    continueOnError: true
    interactive: false
```

Requires the alpha feature to be on, once per machine:

```bash
azd config set alpha.deployment.stacks on
```

Three choices in that block are load-bearing:

**`resources: delete`** means the stack owns the lifecycle. Take a resource out of the template
and the next provision removes it from Azure. `detach` would do the opposite — forget the
resource while leaving it running and billing.

**`resourceGroups: delete`** is why `main.bicep` moved to subscription scope. A resource-group
scoped stack can manage everything *in* a group but never the group itself. Day 23 left
`rg-dispatch-dev` and `rg-dispatch-prod` behind for exactly that reason: a script created them
and no template owned them.

**`denyDelete`, not `denyWriteAndDelete`.** Write-deny would also block the platform's own
legitimate mutations — a Container Apps revision, a scale event, a certificate rotation — and the
application would degrade in ways that look like bugs. Delete is the irreversible operation, so
delete is what gets denied.

### One parameter file, two environments

Day 23 had `main.dev.bicepparam` and `main.prod.bicepparam`, passed explicitly. azd binds one
parameter file to one template, so the choice moves inside it:

```bicep
using './main.bicep'

var envName = readEnvironmentVariable('AZURE_ENV_NAME', 'dev')

var profile = envName == 'prod'
  ? loadJsonContent('profiles/prod.json')
  : loadJsonContent('profiles/dev.json')

param environmentName = envName
param databaseSku      = profile.databaseSku
param serviceBusSku    = profile.serviceBusSku
param minReplicas      = profile.minReplicas
// ... every other knob, straight from the profile
```

`azd env select prod` is the entire promotion mechanism.

This is not the branching Day 23 refused to do. Day 23's rule was that nothing may branch on
`environmentName` to decide a **size**, and that rule holds: there is one conditional and it
selects a whole *profile*, never an individual SKU. Sixteen inline ternaries have to be read
sixteen times to answer "what is prod?" and any one of them can be wrong on its own. Two files
answer it by being diffed, and `scripts/promote.sh` prints that diff before it touches anything:

```
  -> databaseSku          {"name": "GP_S_Gen5_1", ...}   {"name": "GP_Gen5_2", ...}
  -> backupRetentionDays  7                              35
  -> serviceBusSku        "Standard"                     "Premium"
  -> maxDeliveryCount     3                              10
  -> messageTimeToLive    "P1D"                          "P14D"
  -> containerCpu         "0.25"                         "1.0"
  -> minReplicas          0                              2
  -> maxReplicas          2                              10
  -> logRetentionDays     30                             90
  -> logDailyQuotaGb      1                              10
     serviceBusCapacity   1                              1
```

`loadJsonContent` is resolved by the **compiler**, so a missing file or malformed JSON is a build
error on this machine rather than a deployment failure several minutes and one half-created
resource group later. The cost is that the path must be a literal — which is exactly why the
selection is a ternary and not `loadJsonContent('profiles/${envName}.json')`. That would be
tidier and does not compile.

---

## 2. Deploy output — dev

```
Provisioning Azure resources (azd provision)

Subscription: Azure for Students (132ef106-f8ec-4352-83e4-9bc238274f25)
Location: Central India

  WARNING: Feature 'deployment.stacks' is in alpha stage.

Creating a deployment plan
Validating deployment
Creating/Updating resources

  (✓) Done: Resource group: rg-dispatch-dev (1.372s)
  (✓) Done: Service Bus Namespace: sb-dispatch-dev-zgdsji (368ms)
  (✓) Done: Azure SQL Server: sql-dispatch-dev-zgdsji (7.57s)
  (✓) Done: Log Analytics workspace: log-dispatch-dev-zgdsji (22.481s)
  (✓) Done: Application Insights: appi-dispatch-dev-zgdsji (964ms)
  (✓) Done: Container App: ca-dispatch-api-dev (18.718s)

SUCCESS: Your application was provisioned in Azure in 5 minutes 10 seconds.
```

## 3. Deploy output — prod

Promotion is `azd env select prod && azd provision`. Same template, same command, different
environment — wrapped in `scripts/promote.sh`, which refuses to run unless the dev stack is in a
`succeeded` state first. Promoting from an environment that never deployed cleanly promotes
nothing; it just runs an unproven template against expensive resources for the first time.

```
--- 1. Is dev actually in a good state? ---
  azd-stack-dev: succeeded
  managing 16 resources
  gate passed.

--- 4. Provisioning prod ---
  Same template. Same command. Different environment.

Creating/Updating resources

  (✓) Done: Resource group: rg-dispatch-prod (1.57s)
  (✓) Done: Service Bus Namespace: sb-dispatch-prod-r34qlw (701ms)
  (✓) Done: Azure SQL Server: sql-dispatch-prod-r34qlw (6.879s)
  (✓) Done: Application Insights: appi-dispatch-prod-r34qlw (23.715s)
  (✓) Done: Log Analytics workspace: log-dispatch-prod-r34qlw (22.808s)
  (✓) Done: Container App: ca-dispatch-api-prod (16.07s)

SUCCESS: Your application was provisioned in Azure in 3 minutes 24 seconds.
```

Prod is prod-grade, read back **from Azure** rather than from the file that asked for it:

```
  Service Bus SKU      : Premium          (dev: Standard)
  SQL SKU              : GP_Gen5          (dev: GP_S_Gen5 serverless)
  SQL capacity         : 2                (dev: 1)
  SQL backup retention : 35 days          (dev: 7)
  container minReplicas: 2                (dev: 0)
  container CPU        : 1                (dev: 0.25)
  log retention        : 90 days          (dev: 30)
  msg TTL / maxDelivery: P14D / 10        (dev: P1D / 3)
```

---

## 4. What the stack gave, proven rather than asserted

`./scripts/drift-proof.sh dev` — **5 passed, 0 failed**.

### The inventory

```
  state                   : succeeded
  managed                 : 16
  denyMode                : denyDelete
  onUnmanageResources     : delete
  onUnmanageResourceGroups: delete

  managed resources:
    resourceGroups/rg-dispatch-dev
    Microsoft.App/containerApps/ca-dispatch-api-dev
    Microsoft.Authorization/roleAssignments/08fe236e-...
    Microsoft.Authorization/roleAssignments/1181952f-...
    Microsoft.Insights/components/appi-dispatch-dev-zgdsji
    Microsoft.OperationalInsights/workspaces/log-dispatch-dev-zgdsji
    Microsoft.ServiceBus/namespaces/sb-dispatch-dev-zgdsji
    ...topics/work-order-events
    ...topics/work-order-events/subscriptions/billing
    ...topics/work-order-events/subscriptions/billing/rules/only-relevant-events
    ...topics/work-order-events/subscriptions/scheduling
    ...topics/work-order-events/subscriptions/scheduling/rules/only-relevant-events
    Microsoft.Sql/servers/sql-dispatch-dev-zgdsji
    Microsoft.Sql/servers/.../databases/dispatch
    Microsoft.Sql/servers/.../databases/dispatch/backupShortTermRetentionPolicies/default
    Microsoft.Sql/servers/.../firewallRules/AllowAzureServices
```

Worth noting: the two `roleAssignments` are the ones `what-if` reported as **unsupported**,
because their names derive from a principal id that does not exist until the deployment runs.
They deployed, and the inventory is where that gets confirmed.

### Drift prevented — an out-of-band delete is refused

```
$ az servicebus topic delete --name work-order-events ...

ERROR: (DenyAssignmentAuthorizationFailed) The client '<user>' with object id '<guid>'
has permission to perform action 'Microsoft.ServiceBus/namespaces/topics/delete' on scope
'.../topics/work-order-events'; however, the access is denied because of the deny assignment
with name 'Deny assignment '<guid>' created by Deployment Stack '.../azd-stack-dev'.'
```

Read that error carefully: the account **has permission** and is refused anyway. This is not a
policy that reports a violation after the fact and not an RBAC rule that a subscription owner can
shrug off — it is enforced at the control plane, against the portal, the CLI and a 3am incident
alike. The topic was still there afterwards, which is the evidence; the error message is only the
claim.

### Drift corrected — a write is allowed, and the next provision reverts it

`denyDelete` permits writes on purpose, so configuration drift is still possible. The stack does
not prevent it; it makes correcting it a no-argument command rather than an investigation.

```
  tag managedBy before drift    : bicep
  tag managedBy after  drift    : someone-in-the-portal      <- changed outside the template
  re-running: azd provision --no-prompt
  tag managedBy after  provision: bicep                      <- reverted
```

### Teardown — one command, nothing left

```
$ azd down --force

Discovering resources to delete...
Deleting subscription deployment stack azd-stack-prod
  (✓) Done: Deleted subscription deployment stack azd-stack-prod

SUCCESS: Your application was removed from Azure in 8 minutes 46 seconds.
```

azd never enumerates the resources, because it does not need to — the stack already knows. And
afterwards:

```
rg-dispatch-prod exists?                     false
stacks remaining:                            azd-stack-dev
any orphaned prod resources in the sub?      none
rg-dispatch-dev exists?                      true
azd-stack-dev state / manages                succeeded / 16 resources
```

Prod is gone completely, including its resource group. dev, sharing the same subscription and the
same Container Apps environment, is untouched.

### A fifth proof, unplanned

Renaming a firewall rule mid-exercise put the old one outside the stack's inventory. Nobody
deleted it and no script cleaned it up:

```
before:  AllowAllWindowsAzureIps
after :  AllowAzureServices          <- the old rule was removed automatically
```

That is `actionOnUnmanage: delete` doing the job it exists for, on a resource that was dropped
from the template rather than from the stack. Under a plain deployment the old rule would still
be there.

---

## 5. Three bugs that only deploying could find

Day 23 planned cleanly twice — `13 to create, 0 errors`, both environments — and was wrong three
times. None of these are what-if's fault; they are outside what a plan can observe.

### A module race, caused by the fix for a different problem

The first dev provision failed:

```
ResourceNotFound: The Resource 'Microsoft.Insights/components/appi-dispatch-dev-zgdsji'
under resource group 'rg-dispatch-dev' was not found.
```

`api.bicep` reaches the workspace and App Insights with `existing` lookups **by name** rather than
consuming module outputs. Day 23 chose that deliberately, and for a good reason: a module output
is unknown until that module runs, so consuming one makes the downstream graph unresolvable at
plan time and what-if reports `NestedDeploymentShortCircuited` instead of a plan. That fix is what
took Day 23 from *12-to-create-with-2-skipped* to *13-with-0-skipped*.

What it also removed, invisibly, was the **dependency**. ARM infers ordering from data flow —
module B waits for A because it consumes A's output. An `existing` reference is not data flow, so
ARM saw no relationship and ran both modules in parallel. `api` started before `observability`
finished.

what-if never caught it and never could: it evaluates the template, not the schedule ARM will run
it on. The fix states the dependency without reintroducing the data flow:

```bicep
module api 'modules/api.bicep' = {
  // ...
  dependsOn: [
    observability
  ]
}
```

Plan-time resolvability and correct ordering are both available. They just have to be asked for
separately.

### A SKU that cannot do what the parameter file asked of it

Day 23's prod profile paired `S1` with `sqlZoneRedundant: true`, and what-if accepted it. That
combination cannot deploy — zone redundancy is unsupported on the DTU-based Standard tier — because
what-if validates template *shape*, not whether a SKU supports the features requested alongside it.
Changed to `GP_Gen5_2`, the smallest vCore SKU that does support it.

Then prod failed anyway:

```
ProvisioningDisabled: Provisioning of zone redundant database/pool is not supported
for your current request.
```

That message names neither the region nor the SKU, so both were checked rather than guessed:

```
az account list-locations   ->  centralindia reports availability zones 1, 2, 3
az sql db list-editions     ->  GP_Gen5_2 reports zoneRedundant=True, status=Default
```

Both support it. The **subscription** does not — an Azure for Students offer does not permit
zone-redundant provisioning. So prod runs with `sqlZoneRedundant: false` and the profile says why
at length. What is actually lost: a single datacentre failure takes the production database down,
and the backup storage drops to locally-redundant with it, so the recovery path shares a failure
domain with the thing it is meant to recover.

### `azd down --purge` is incompatible with deny settings

```
Purging Log Analytics Workspace: log-dispatch-prod-r34qlw
  (x) Failed

RESPONSE 403: DenyAssignmentAuthorizationFailed
... the access is denied because of the deny assignment ... created by Deployment Stack
'.../deploymentStacks/azd-stack-prod'
```

`--purge` permanently purges soft-deletable resources, and it does so with a **direct** delete
issued before the stack is unmanaged — precisely the path the deny assignment blocks. The
protection is working exactly as configured; the flag is what is incompatible.

`azd down --force` succeeds, because deletes that travel *through* the stack are permitted: the
stack removes its own deny assignment first, then removes the resources. The cost is that the Log
Analytics workspace is left **soft-deleted** rather than purged. It bills nothing in that state and
ages out on its own, and it is the one thing this teardown did not fully erase.

---

## 6. Honest gaps

- **The postprovision hook does not run on `azd provision`.** It was written so the T-SQL grant
  could not be forgotten, and measurement says it is not automatic: two fully captured clean
  provisions produced no hook output at all, while `azd hooks run postprovision` executes it
  correctly and prints the pending statement. The grant is back to being a step someone has to remember. The hook is
  kept because the workaround is one line, but calling it automatic would be a claim the evidence
  does not support.
- **The database grant was never applied**, because `sqlcmd` is not installed on this machine. The
  API can reach SQL and cannot read from it. The hook reports this rather than pretending
  otherwise.
- **No application was deployed.** `azure.yaml` has no `services:` block, so this is `azd provision`
  only — the Container App runs `mcr.microsoft.com/k8se/quickstart:latest`. Wiring the Day 22
  piece 2 code in needs a container registry the template does not create.
- **Prod no longer exists.** It was deployed, verified against Azure, and torn down to stop
  Premium Service Bus and a provisioned SQL database billing against a student subscription. Every
  prod number above came from the live environment; none of it is running now.
- **Prod shares dev's region and Container Apps environment**, because this subscription permits
  exactly one environment in total. A regional outage would take both.
- **`--preview` does not work with stacks** — `azd provision --preview` returns
  `preview not supported`, so the plan came from `az deployment sub what-if` against the same
  template and parameters.
- **`deployment.stacks` is alpha** in azd 1.31.1 and prints a warning on every command.
