# Day 23 — what I built, where, and why

Day 23 is a new folder rather than a delta on a previous day: it is infrastructure, not
application code. This lists every file, the decision it carries, and *why that choice and not the
obvious alternative*.

Nothing in `Day17`–`Day22` was modified.

---

## Definitions first

**Infrastructure as Code (IaC)** — describing what should exist in a file that is reviewed, diffed
and version-controlled, rather than clicking it into being. The value is not automation; it is that
the description is **the** source of truth, so "what is deployed" and "what we think is deployed"
cannot drift apart silently.

**Bicep** — a domain-specific language that compiles to ARM JSON. Same deployment engine, far less
ceremony, and — the part that matters here — a **type checker**. A misspelled property is a
compile error rather than a deployment that half-succeeds.

**Declarative, not imperative.** The template describes the desired end state; ARM works out the
operations. Running it twice is the same as running it once, which is what makes it safe to run
from CI on every merge.

**Idempotent** — the property that follows from that. Every name in this template is derived
deterministically for exactly this reason: a run that generates a fresh name is not idempotent, it
just looks like it until the second run.

**`what-if`** — a read-only plan. ARM resolves the template against the current state of the
resource group and reports the diff. It creates nothing, so it is safe against production, and it
is the only honest way to review an infrastructure change before making it.

**Module** — a `.bicep` file called from another with parameters, deployed as a nested deployment.
The unit of reuse, and — as §6 explains — the unit of *evaluation*, which turns out to matter more.

**`.bicepparam`** — a typed parameter file bound to one template with `using`. The compiler checks
it. A JSON parameter file is untyped text that only fails once ARM sees it.

---

## 1. The shape

**Files:** `Day23/infra/`

```
main.bicep                composition root — naming, wiring, outputs
main.dev.bicepparam       cheap, disposable, scale-to-zero
main.prod.bicepparam      durable, always-warm, zone-redundant
modules/
  sql.bicep               Entra-only auth
  servicebus.bicep        topic + 2 filtered subscriptions
  api.bicep               Container App, system-assigned identity
  observability.bicep     Log Analytics + App Insights
  rbac.bicep              role assignments
```

The exercise names three modules. There are five, and both extras earn their place:

**`observability.bicep`** — a Container Apps managed environment **cannot be created** without a
Log Analytics workspace. Leaving it out does not produce a smaller template, it produces one that
cannot deploy. It also closes Day 22 piece 1's recorded gap: *"No metrics export … observable only
if somebody asks."*

**`rbac.bicep`** — a separate module because the compiler insists. See §6.

---

## 2. No secrets — the decision the whole template is built around

This is the continuation of Day 17, which proved a managed identity could replace a client secret
for one API call. Day 23 applies the same idea to a database and a broker.

### SQL — `modules/sql.bicep`

```bicep
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
    minimalTlsVersion: '1.2'
  }
}
```

**Why not the obvious alternative.** The standard template has
`administratorLogin` + `administratorLoginPassword`, the password marked `@secure()`, sourced from
Key Vault. That is the pattern most tutorials teach, and it is *worse*, because the credential
still exists — it has merely been moved somewhere that requires more machinery to read. Entra-only
auth deletes it. There is no password to rotate, leak, or store.

**A group, not a person.** A named individual becomes an orphaned admin the day they change roles.

**The connection string is a plain output**, not a `@secure()` one:

```bicep
output connectionString string = join([
  'Server=tcp:${fqdn},1433'
  'Initial Catalog=dispatch'
  'Authentication=Active Directory Default'   // ← the driver fetches a token
], ';')
```

It carries no credential, so marking it secure would be theatre — and would make it invisible in
`what-if`, which is worse.

### Service Bus — `modules/servicebus.bicep`

```bicep
properties: {
  disableLocalAuth: true
  minimumTlsVersion: '1.2'
}
```

One line switches SAS keys off entirely. The namespace has no connection string to copy into an
app setting and nothing to rotate. **Same decision as Day 17's `--admin-enabled false` on the
container registry: the convenient path writes a credential into an app setting, so the convenient
path is closed.**

### The Container App — `modules/api.bicep`

```bicep
identity: { type: 'SystemAssigned' }
...
configuration: {
  secrets: []      // empty, and that is the deliverable
}
```

### The claim, and how it is checkable

Zero `Microsoft.KeyVault` resources; zero `secureString` parameters. Both are visible in the
`what-if` output. That is a stronger statement than "we keep our secrets safe", because there is
nothing to keep.

---

## 3. Parameterisation — what "parameterized" actually has to mean

**File:** `main.bicep`

The rule the template follows: **nothing branches on `environmentName` to decide a size.**

```bicep
param databaseSku object
param minReplicas int
param maxDeliveryCount int
```

**Why not the obvious alternative.** The tempting shortcut is:

```bicep
sku: environmentName == 'prod' ? { name: 'S1', … } : { name: 'GP_S_Gen5_1', … }
```

That module now has **two behaviours and one name**. Adding a third environment means editing
every ternary in every module, and the parameter file — the thing a reviewer actually reads —
stops describing what will be deployed. A module that takes a SKU object has one behaviour and is
honest about what varies.

`environmentName` survives only where it belongs: in resource **names**.

### The differences are real

| | dev | prod | why |
|---|---|---|---|
| SQL | `GP_S_Gen5_1` serverless | `S1` provisioned | auto-pause is right for dev and catastrophic for prod — the first request after a pause waits seconds, and it arrives when traffic is lightest |
| min replicas | **0** | **2** | 0 = an idle dev environment bills no compute. 2 = the smallest number that makes a rolling restart invisible |
| Service Bus | Standard | Premium | not throughput — dedicated resources, so tail latency is not somebody else's traffic |
| max delivery | 3 | 10 | dev wants poison messages in the DLQ fast; prod's failures are usually transient outages |
| log cap | 1 GB/day | 10 GB/day | a cap is a blast radius, not a budget |

---

## 4. `.bicepparam` over JSON, and the feature that decided it

```bicep
using './main.bicep'
param environmentName = 'dev'
```

`using` binds the file to the template, so **the compiler checks it**: a misspelled parameter, a
missing required one, or a value outside an `@allowed` list is an error before anything is
submitted. JSON parameter files are untyped text that fail several minutes into a deployment.

The feature that settled it:

```bicep
param sqlAdminObjectId = readEnvironmentVariable('SQL_ADMIN_OBJECT_ID', '00000000-…')
param existingManagedEnvironmentId = readEnvironmentVariable('CONTAINER_APP_ENV_ID', '')
```

Neither value is a secret — an object id and a resource id are public identifiers — but both are
specific to one directory and one subscription, so a committed value is wrong for everyone else.
CI supplies them as pipeline variables. **JSON parameter files cannot express this at all.** The
fallback keeps `bicep build-params` working offline for anyone who just wants to type-check.

---

## 5. The Service Bus topology comes from the design, not a gallery

**File:** `modules/servicebus.bicep`

**One topic per publishing aggregate, not per event type.** A topic per event multiplies
infrastructure by the size of the domain and gives every new event a deployment.

**One subscription per consuming module**, named after the module that owns it — `scheduling` and
`billing`, which are exactly the two modules that subscribe in Day 22 piece 2.

**A SQL filter on each:**

```bicep
sqlExpression: 'eventType IN (${join(map(sub.eventTypes, t => '\'${t}\''), ', ')})'
```

Without a rule, every subscription receives **every** message and each consumer deserialises and
discards what it does not want — paying delivery cost for messages it will never act on. The
filter reads `eventType` from the application properties, which Dispatch's publisher sets
*precisely so this works without touching the body*.

**`requiresDuplicateDetection: false`, deliberately.** Broker dedupe protects against a
*publisher* retrying inside a short window and nothing else. It does not protect against the case
that matters — a consumer that did the work and died before settling. Only the consumer can defend
against that, and Dispatch's handlers already do. Turning it on would mask the behaviour without
solving it.

**`deadLetteringOnMessageExpiration: true`.** A message nobody consumed before its TTL is evidence
of a broken consumer; silently discarding it destroys the evidence.

---

## 6. The two bugs this exercise caught

### 6.1 `BCP120` — role assignments cannot use runtime values

The role assignments were four lines in `main.bicep` first, and the template did not compile:

```
BCP120: This expression is being used in an assignment to the "scope" property of the
"Microsoft.Authorization/roleAssignments" type, which requires a value that can be calculated
at the START of the deployment.
```

A role assignment's `scope` and `name` are part of its identity, so ARM must know them before
submitting anything. `api.outputs.principalId` is only known once that module has *run*.

**Fixed with a module boundary** (`modules/rbac.bicep`), because a module's **parameters** are by
definition known at the start of *that module's* deployment. The values still arrive at runtime;
they are simply resolved one deployment earlier.

Two details inside it are load-bearing:

```bicep
name: guid(namespace.id, principalId, roleId)   // DETERMINISTIC
principalType: 'ServicePrincipal'
```

The deterministic name is what makes redeployment idempotent — a fresh GUID per run means the
second deploy fails with `RoleAssignmentExists`. And `principalType` stops a deployment that runs
before the identity has replicated through Entra from failing with *"principal does not exist"* —
an error that is transient, misleading, and vanishes on retry.

### 6.2 The plan that looked clean because a third of it was skipped

**This is the one worth keeping.** The first what-if reported **12 to create** and two
diagnostics:

```
(NestedDeploymentShortCircuited) A nested deployment got short-circuited and all its resources
got skipped from validation. This is due to a nested template having a parameter that was not
fully evaluated (e.g. contains a reference() function).
```

The `api` module — the Container App, its environment, its scaling — **was not being planned at
all.** The plan was green because a third of it had been silently skipped.

The cause was passing module *outputs* as module *parameters*:

```bicep
logAnalyticsWorkspaceId:     observability.outputs.workspaceId
appInsightsConnectionString: observability.outputs.appInsightsConnectionString
sqlConnectionString:         sql.outputs.connectionString
```

Every one is a `reference()` under the hood, unresolvable until the producing module runs.

**Fixed by passing names and deriving everything else.** Naming moved out of the modules and into
`main.bicep`:

```bicep
var sqlServerName = 'sql-dispatch-${environmentName}-${nameSuffix}'
var workspaceName = 'log-dispatch-${environmentName}-${nameSuffix}'

var sqlServerFqdn = '${sqlServerName}${environment().suffixes.sqlServerHostname}'
```

and `api.bicep` resolves what it needs by name:

```bicep
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: logAnalyticsWorkspaceName
}
...
customerId: workspace.properties.customerId
sharedKey: workspace.listKeys().primarySharedKey
```

`listKeys()` is still a runtime function — but it is now a *property value inside* the module,
which what-if renders as unknown, rather than an unevaluated *parameter to* the module, which
stops the whole thing being planned.

**Result: 12 with two modules skipped → 13 with none.**

The generalisable rule, and it is a design constraint rather than a trick:

> **Pass names down; derive ids locally.** A name is computable at compile time. An id taken from
> a module output is not, and using one costs you the plan for everything downstream.

### The residual `Unsupported`, which is inherent

```
(Unsupported) Changes to the resource ... cannot be analyzed because its resource ID or API
version cannot be calculated until the deployment is under way.
```

Both are the role assignments. Their name is `guid(scope, principalId, roleId)`, and `principalId`
belongs to a system-assigned identity that does not exist until the Container App is created. No
restructuring changes that; a user-assigned identity would have the same problem. **Role
assignments to a just-created identity are never fully plannable**, and saying so is more useful
than pretending otherwise.

---

## 7. Constraints this subscription imposed

Both were discovered by what-if rather than by reading documentation, which is the argument for
planning before deploying.

**`MaxNumberOfGlobalEnvironmentsInSubExceeded`** — this subscription permits **one** Container Apps
environment in total, not one per region. Prod was written to deploy into `koreacentral` with its
own environment; it cannot. Both environments now reuse the existing one via
`existingManagedEnvironmentId`, and `main.prod.bicepparam` records why in full. On a subscription
without the cap, `location` becomes a different region and nothing else changes.

**`sys.regionrestriction`** — five permitted regions, discovered on Day 17. Encoded as an
`@allowed` list on the `location` parameter, which turns a runtime `RequestDisallowedByAzure` into
a parameter-validation error before anything is submitted.

---

## 8. The step Bicep cannot perform

Granting the managed identity a **database user** is T-SQL executed inside the database. There is
no ARM resource for it:

```sql
CREATE USER [ca-dispatch-api-dev] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [ca-dispatch-api-dev];
ALTER ROLE db_datawriter ADD MEMBER [ca-dispatch-api-dev];
ALTER ROLE db_ddladmin  ADD MEMBER [ca-dispatch-api-dev];
```

Emitted as a **template output** (`grantDatabaseAccessScript`) and printed by `deploy.sh` rather
than left as tribal knowledge. Until it runs, the API starts, answers `/health`, and fails every
query with *"Login failed for user '&lt;token-identified principal&gt;'"* — which reads like a
credential problem and is a missing grant.

---

## Files

| Path | What it carries |
|---|---|
| `infra/main.bicep` | composition root: **all naming**, module wiring, outputs |
| `infra/main.dev.bicepparam` | serverless SQL, scale-to-zero, 1 GB/day log cap |
| `infra/main.prod.bicepparam` | provisioned SQL, zone-redundant, min 2 replicas |
| `infra/modules/sql.bicep` | **Entra-only auth — no admin password exists** |
| `infra/modules/servicebus.bicep` | topic, 2 filtered subscriptions, local auth disabled |
| `infra/modules/api.bicep` | Container App, system-assigned identity, `secrets: []` |
| `infra/modules/observability.bicep` | Log Analytics + App Insights, with an ingestion cap |
| `infra/modules/rbac.bicep` | role assignments — separate because `BCP120` requires it |
| `scripts/whatif.sh` | compiles, then plans. Read-only. |
| `scripts/deploy.sh` | plans, confirms, deploys, prints the manual grant |
| `docs/whatif-dev.txt` · `docs/whatif-prod.txt` | captured plans |
