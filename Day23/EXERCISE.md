# Day 23 — Bicep IaC

> **Exercise:** Paste the main Bicep + one module + the dev/prod params. Show a successful
> what-if/deploy output.

**Result:** both environments plan cleanly — **13 resources to create, 0 errors, 0 short-circuited
modules**. Captured in [`docs/whatif-dev.txt`](docs/whatif-dev.txt) and
[`docs/whatif-prod.txt`](docs/whatif-prod.txt).

---

## What this describes

The infrastructure for the **Day 22 piece 2 capstone** (`Dispatch`). That is not an arbitrary
pairing — every resource below closes a gap the capstone recorded against itself:

| Gap, in the capstone's own words | Closed by |
|---|---|
| *"No persistence. All three stores are dictionaries."* | `modules/sql.bicep` |
| *"Replace `InProcessIntegrationEventPublisher` with a Service Bus topic."* | `modules/servicebus.bicep` |
| *"No metrics export … observable only if somebody asks."* (Day 22 p1) | `modules/observability.bicep` |

## The property worth defending

**There are no secrets in this deployment.** Not stored, not referenced from Key Vault, not passed
as secure parameters — none exist to store.

| Resource | How access works | What is *not* there |
|---|---|---|
| Azure SQL | Entra-only auth, `azureADOnlyAuthentication: true` | no `administratorLoginPassword` |
| Service Bus | `disableLocalAuth: true` + RBAC role assignments | no SAS connection string |
| Container App | system-assigned managed identity | `secrets: []` — empty, not omitted |

The `what-if` output contains zero `Microsoft.KeyVault` resources and the template declares zero
`secureString` parameters. That is a stronger claim than "we keep our secrets safe", because there
is nothing to keep.

It is also the direct continuation of Day 17, which proved the same idea for one API call. Day 23
applies it to a database and a broker.

---

## 1. `infra/main.bicep`

```bicep
targetScope = 'resourceGroup'

@allowed([ 'dev', 'prod' ])
param environmentName string

@description('''
Deployment region.

Constrained to five because this subscription carries a `sys.regionrestriction` policy that
permits only those, and Day 17 spent a deployment discovering it the hard way. Encoding the
allow-list here turns a runtime `RequestDisallowedByAzure` into a parameter-validation error
before anything is submitted.
''')
@allowed([ 'centralindia', 'indonesiacentral', 'malaysiawest', 'uaenorth', 'koreacentral' ])
param location string = 'centralindia'

param sqlAdminObjectId string
param sqlAdminLogin string
param existingManagedEnvironmentId string = ''
param containerImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

// ---- per-environment sizing. Nothing branches on environmentName to decide a SIZE. ----
param databaseSku object
param backupRetentionDays int
param sqlZoneRedundant bool
param serviceBusSku string
param serviceBusCapacity int = 1
param maxDeliveryCount int
param messageTimeToLive string
param containerResources object
param minReplicas int
param maxReplicas int
param logRetentionDays int
param logDailyQuotaGb int

var nameSuffix = take(uniqueString(resourceGroup().id), 6)

// Every resource name lives HERE, in the composition root, not inside the module that creates
// it. A name built inside a module is unknown until that module runs, so any value derived from
// it is also unknown — and what-if reports NestedDeploymentShortCircuited and validates nothing
// downstream. See §5.
var sqlServerName           = 'sql-dispatch-${environmentName}-${nameSuffix}'
var serviceBusNamespaceName = 'sb-dispatch-${environmentName}-${nameSuffix}'
var workspaceName           = 'log-dispatch-${environmentName}-${nameSuffix}'
var appInsightsName         = 'appi-dispatch-${environmentName}-${nameSuffix}'

var sqlServerFqdn  = '${sqlServerName}${environment().suffixes.sqlServerHostname}'
var serviceBusFqdn = '${serviceBusNamespaceName}.servicebus.windows.net'

var sqlConnectionString = join([
  'Server=tcp:${sqlServerFqdn},1433'
  'Initial Catalog=dispatch'
  'Encrypt=True'
  'TrustServerCertificate=False'
  'Connection Timeout=30'
  'Authentication=Active Directory Default'   // ← no password. The driver fetches a token.
], ';')

var tags = {
  application: 'dispatch'
  environment: environmentName
  managedBy: 'bicep'
  source: 'Day23/infra/main.bicep'
}

module observability 'modules/observability.bicep' = {
  name: 'observability'
  params: {
    location: location
    workspaceName: workspaceName
    appInsightsName: appInsightsName
    retentionDays: logRetentionDays
    dailyQuotaGb: logDailyQuotaGb
    tags: tags
  }
}

module sql 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    location: location
    serverName: sqlServerName
    databaseSku: databaseSku
    sqlAdminObjectId: sqlAdminObjectId
    sqlAdminLogin: sqlAdminLogin
    backupRetentionDays: backupRetentionDays
    zoneRedundant: sqlZoneRedundant
    tags: tags
  }
}

module serviceBus 'modules/servicebus.bicep' = {
  name: 'servicebus'
  params: {
    location: location
    namespaceName: serviceBusNamespaceName
    skuName: serviceBusSku
    messagingUnits: serviceBusCapacity
    maxDeliveryCount: maxDeliveryCount
    messageTimeToLive: messageTimeToLive
    tags: tags
  }
}

module api 'modules/api.bicep' = {
  name: 'api'
  params: {
    location: location
    environmentName: environmentName
    nameSuffix: nameSuffix
    logAnalyticsWorkspaceName: workspaceName
    appInsightsName: appInsightsName
    existingManagedEnvironmentId: existingManagedEnvironmentId
    containerImage: containerImage
    containerResources: containerResources
    minReplicas: minReplicas
    maxReplicas: maxReplicas
    sqlConnectionString: sqlConnectionString      // endpoints, not credentials
    serviceBusNamespace: serviceBusFqdn
    serviceBusTopic: 'work-order-events'
    tags: tags
  }
}

// RBAC is a separate module for a reason the compiler enforces — see §5.
module rbac 'modules/rbac.bicep' = {
  name: 'rbac'
  params: {
    serviceBusNamespaceName: serviceBusNamespaceName
    principalId: api.outputs.principalId
  }
}

output apiUrl string = api.outputs.apiUrl
output sqlServerFqdn string = sqlServerFqdn
output serviceBusNamespace string = serviceBusFqdn

@description('Run this against the database, signed in as the SQL admin, to finish the wiring.')
output grantDatabaseAccessScript string = join([
  'CREATE USER [${api.outputs.apiName}] FROM EXTERNAL PROVIDER;'
  'ALTER ROLE db_datareader ADD MEMBER [${api.outputs.apiName}];'
  'ALTER ROLE db_datawriter ADD MEMBER [${api.outputs.apiName}];'
  'ALTER ROLE db_ddladmin  ADD MEMBER [${api.outputs.apiName}];'
], ' ')
```

---

## 2. One module — `infra/modules/servicebus.bicep`

Chosen over SQL because its topology is a direct translation of the capstone's design rather
than a generic resource declaration.

```bicep
@description('Namespace SKU. Standard is the MINIMUM that supports topics — Basic gives queues only.')
@allowed([ 'Standard', 'Premium' ])
param skuName string

@description('How many times a message is delivered before the broker dead-letters it itself.')
@minValue(1) @maxValue(100)
param maxDeliveryCount int

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: namespaceName
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuName
    capacity: skuName == 'Premium' ? messagingUnits : null
  }
  properties: {
    // The line that matters. Switches OFF SAS keys entirely, so the namespace has no connection
    // string to copy into an app setting and nothing to rotate. Access is Entra RBAC only.
    //
    // Same decision as Day 17's `--admin-enabled false` on the registry: the convenient path
    // writes a credential into an app setting, so the convenient path is closed.
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    zoneRedundant: skuName == 'Premium'
  }
}

// One topic per PUBLISHING AGGREGATE, not one per event. A topic per event type multiplies
// infrastructure by the size of the domain; this keeps the count bounded and lets subscribers
// filter — which is what the rules below are for.
resource workOrderEvents 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: namespace
  name: 'work-order-events'
  properties: {
    defaultMessageTimeToLive: messageTimeToLive
    enablePartitioning: skuName == 'Standard'

    // Deliberately OFF. Day 19 explains at length: broker dedupe only protects against a
    // PUBLISHER retrying inside a short window. It does nothing about the case that matters —
    // a consumer that did the work and died before settling. Only the consumer can defend
    // against that, and Dispatch's handlers already do.
    requiresDuplicateDetection: false
  }
}

// One subscription per CONSUMING MODULE, each named after the module that owns it. Every
// subscription gets its own copy of every message, its own delivery count and its OWN
// dead-letter queue — which is the entire reason to pay for a topic rather than a queue.
var subscriptions = [
  { name: 'scheduling', eventTypes: [ 'WorkOrderScheduledV1', 'WorkOrderReleasedV1' ] }
  { name: 'billing',    eventTypes: [ 'WorkOrderCompletedV1' ] }
]

resource topicSubscriptions '…/subscriptions@2022-10-01-preview' = [for sub in subscriptions: {
  parent: workOrderEvents
  name: sub.name
  properties: {
    maxDeliveryCount: maxDeliveryCount
    defaultMessageTimeToLive: messageTimeToLive

    // A message nobody consumed before its TTL is evidence of a broken consumer, and silently
    // discarding it destroys that evidence.
    deadLetteringOnMessageExpiration: true
    deadLetteringOnFilterEvaluationExceptions: true
    lockDuration: 'PT1M'
  }
}]

// Without a rule a subscription receives EVERY message and each consumer deserialises and
// discards what it does not want. The filter reads `eventType` from the application properties,
// which Dispatch's publisher sets precisely so this works WITHOUT touching the body.
resource subscriptionRules '…/rules@2022-10-01-preview' = [for (sub, i) in subscriptions: {
  parent: topicSubscriptions[i]
  name: 'only-relevant-events'
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: {
      sqlExpression: 'eventType IN (${join(map(sub.eventTypes, t => '\'${t}\''), ', ')})'
    }
  }
}]
```

---

## 3. The parameter files

`.bicepparam`, not JSON. The difference is not cosmetic: `using` binds the file to `main.bicep`,
so **the compiler checks it** — a misspelled parameter, a missing required one, or a value outside
an `@allowed` list is an error before anything reaches Azure. A JSON parameter file is untyped
text that fails at deployment time, several minutes and one half-created resource group later.

It also enables `readEnvironmentVariable`, which JSON cannot express — used below for the two
values that are identifiers rather than settings.

### `infra/main.dev.bicepparam`

```bicep
using './main.bicep'

param environmentName = 'dev'
param location = 'centralindia'

// SERVERLESS. Auto-pauses after an hour idle, so an environment nobody is using stops billing
// for compute. The cost is a cold start of several seconds — the right trade for dev, and
// exactly the wrong one for prod.
param databaseSku = { name: 'GP_S_Gen5_1', tier: 'GeneralPurpose', family: 'Gen5', capacity: 1 }
param backupRetentionDays = 7
param sqlZoneRedundant = false

// Standard is the MINIMUM that supports topics. Basic is cheaper and gives queues only, which
// cannot express the fan-out the design depends on — the one place dev cannot economise without
// changing the architecture being tested.
param serviceBusSku = 'Standard'
param serviceBusCapacity = 1

// LOW on purpose: a poison message should reach the DLQ quickly so it can be looked at.
param maxDeliveryCount = 3
param messageTimeToLive = 'P1D'

// minReplicas: 0 is the single largest cost saving in this file. An idle dev environment runs
// no containers and bills no compute.
param containerResources = { cpu: json('0.25'), memory: '0.5Gi' }
param minReplicas = 0
param maxReplicas = 2

param logRetentionDays = 30
param logDailyQuotaGb = 1   // a cap is a blast radius, not a budget

param sqlAdminObjectId = readEnvironmentVariable('SQL_ADMIN_OBJECT_ID', '00000000-…')
param sqlAdminLogin = 'sql-dispatch-admins'
param existingManagedEnvironmentId = readEnvironmentVariable('CONTAINER_APP_ENV_ID', '')
```

### `infra/main.prod.bicepparam`

```bicep
using './main.bicep'

param environmentName = 'prod'
param location = 'centralindia'   // see §6 — this SHOULD be a different region

// PROVISIONED, not serverless. The most consequential line in the file: auto-pause is what makes
// dev cheap and is exactly what must not happen in production — the first request after a pause
// waits several seconds, and it arrives at whatever hour traffic is lightest.
param databaseSku = { name: 'S1', tier: 'Standard', capacity: 20 }
param backupRetentionDays = 35        // the Azure maximum
param sqlZoneRedundant = true

// Premium, not for throughput but for the other three things Standard cannot give: dedicated
// resources so a noisy neighbour cannot affect latency, zone redundancy, and predictable cost.
param serviceBusSku = 'Premium'
param serviceBusCapacity = 1

// Ten, against dev's three. A production consumer failing is far more likely to be a transient
// downstream outage than a poison message, and burning the retry budget in thirty seconds turns
// a recoverable blip into a DLQ a human has to drain.
param maxDeliveryCount = 10
param messageTimeToLive = 'P14D'

// minReplicas: 2, not 1. One replica means every deployment and every crash is a total outage.
// Two is the smallest number that makes a rolling restart invisible.
param containerResources = { cpu: json('1.0'), memory: '2Gi' }
param minReplicas = 2
param maxReplicas = 10

param logRetentionDays = 90
param logDailyQuotaGb = 10

param sqlAdminObjectId = readEnvironmentVariable('SQL_ADMIN_OBJECT_ID', '00000000-…')
param sqlAdminLogin = 'sql-dispatch-admins-prod'   // a DIFFERENT group from dev
param existingManagedEnvironmentId = readEnvironmentVariable('CONTAINER_APP_ENV_ID', '')
```

### The differences, read back out of the two plans

Not asserted — this is `grep` over the captured what-if output:

| | dev | prod |
|---|---|---|
| SQL SKU | `GP_S_Gen5_1` (serverless) | `S1` (provisioned) |
| auto-pause | `60` min | `-1` (disabled) |
| SQL zone redundant | `false` | `true` |
| backup retention | 7 days | 35 days |
| Service Bus SKU | `Standard` | `Premium` |
| max delivery count | 3 | 10 |
| message TTL | `P1D` | `P14D` |
| container CPU | `0.25` | `1.0` |
| min replicas | `0` | `2` |
| log retention | 30 days | 90 days |
| daily log cap | 1 GB | 10 GB |

---

## 4. The what-if output

```
$ export SQL_ADMIN_OBJECT_ID=<entra-group-object-id>
$ export CONTAINER_APP_ENV_ID=<existing container apps environment id>
$ az deployment group what-if --resource-group rg-dispatch-dev \
      --template-file main.bicep --parameters main.dev.bicepparam

Resource and property changes are indicated with these symbols:
  + Create
  x Unsupported

  + Microsoft.App/containerApps/ca-dispatch-api-dev
  + Microsoft.Insights/components/appi-dispatch-dev-zgdsji
  + Microsoft.OperationalInsights/workspaces/log-dispatch-dev-zgdsji
  + Microsoft.ServiceBus/namespaces/sb-dispatch-dev-zgdsji
  + Microsoft.ServiceBus/namespaces/sb-dispatch-dev-zgdsji/topics/work-order-events
  + …/topics/work-order-events/subscriptions/billing
  + …/topics/work-order-events/subscriptions/billing/rules/only-relevant-events
  + …/topics/work-order-events/subscriptions/scheduling
  + …/topics/work-order-events/subscriptions/scheduling/rules/only-relevant-events
  + Microsoft.Sql/servers/sql-dispatch-dev-zgdsji
  + Microsoft.Sql/servers/sql-dispatch-dev-zgdsji/databases/dispatch
  + …/databases/dispatch/backupShortTermRetentionPolicies/default
  + Microsoft.Sql/servers/sql-dispatch-dev-zgdsji/firewallRules/AllowAllWindowsAzureIps

Resource changes: 13 to create, 2 unsupported.
```

A representative resource, showing the properties that carry the design:

```
  + Microsoft.Sql/servers/sql-dispatch-dev-zgdsji [2023-08-01-preview]
      properties.administrators.administratorType:         "ActiveDirectory"
      properties.administrators.azureADOnlyAuthentication: true          ← no password exists
      properties.administrators.principalType:             "Group"
      properties.minimalTlsVersion:                        "1.2"

  + Microsoft.Sql/servers/…/databases/dispatch
      properties.autoPauseDelay:   60                                    ← dev; prod is -1
      sku.name:                    "GP_S_Gen5_1"                          ← dev; prod is S1
```

`prod` produces the same 13 resources with the different property values in the table above.

---

## 5. Two things what-if could not do, and what was done about them

### The one that was fixable: `NestedDeploymentShortCircuited`

The first run reported **12 to create** and two diagnostics:

```
(NestedDeploymentShortCircuited) A nested deployment got short-circuited and all its resources
got skipped from validation. This is due to a nested template having a parameter that was not
fully evaluated (e.g. contains a reference() function).
```

The `api` module was **not being planned at all** — the plan looked clean because a third of it
was silently skipped. The cause was passing module *outputs* as module *parameters*:

```bicep
logAnalyticsWorkspaceId:     observability.outputs.workspaceId          // a reference()
appInsightsConnectionString: observability.outputs.appInsightsConnectionString
sqlConnectionString:         sql.outputs.connectionString
```

Fixed by **passing names and deriving everything else**:

- naming moved into `main.bicep`, so every name is a compile-time `var`
- `api.bicep` resolves the workspace and the App Insights component with `existing` by name,
  rather than receiving their ids
- the SQL connection string is built in `main.bicep` from `environment().suffixes.sqlServerHostname`

That took the plan from **12 to 13 resources with two modules skipped** → **13 with none**. The
generalisable rule: **pass names down, derive ids locally.** An id taken from an output is a
runtime value; a name is not.

### The one that is inherent: `Unsupported`

```
(Unsupported) Changes to the resource ... cannot be analyzed because its resource ID or API
version cannot be calculated until the deployment is under way.
```

Both are the Service Bus role assignments. Their *name* is `guid(scope, principalId, roleId)`,
and `principalId` belongs to a system-assigned identity that **does not exist until the Container
App is created**. No amount of restructuring changes that — it is a property of system-assigned
identities, not a template defect.

A user-assigned identity created earlier in the same deployment would have the same problem: its
`principalId` is still a runtime value. The honest position is that role assignments to a
just-created identity are never fully plannable.

### Why RBAC is a separate module at all

It was four lines in `main.bicep` first, and it did not compile:

```
BCP120: This expression is being used in an assignment to the "scope" property of the
"Microsoft.Authorization/roleAssignments" type, which requires a value that can be calculated
at the START of the deployment.
```

A role assignment's `scope` and `name` are part of its identity, so ARM must know them before
submitting anything. A module boundary fixes it because a module's **parameters** are by
definition known at the start of that module's own deployment. The values still arrive at
runtime; they are resolved one deployment earlier.

---

## 6. Honest gaps

**Prod shares dev's region and its Container Apps environment.** It should not. The first attempt
put prod in `koreacentral` and what-if returned:

```
MaxNumberOfGlobalEnvironmentsInSubExceeded - The subscription cannot have more than
1 Container App Environments.
```

Not one per region — **one, across the whole subscription**. So prod cannot have an environment of
its own anywhere. Sharing a region means a regional outage takes both, so the environment meant to
rehearse a recovery disappears at exactly the moment it is needed. On a subscription without that
cap, `location` becomes a different region and nothing else changes.

**The database grant is not in the template.** `CREATE USER … FROM EXTERNAL PROVIDER` is T-SQL
executed inside the database; there is no ARM resource for it. It is emitted as a template output
and run by `deploy.sh` rather than left as tribal knowledge — but it is still a manual step, and
until it runs the API starts, answers `/health`, and fails every query with *"Login failed for
user '&lt;token-identified principal&gt;'"*, which reads like a credential problem and is a missing
grant.

**SQL is publicly reachable**, with a firewall rule allowing Azure services. A private endpoint is
the right answer for real production and is out of scope here.

**No CI.** The template is deployed from a workstation. Day 17 already has the OIDC federated-
credential pattern that would fix this; wiring it in is the obvious next step.

**Nothing has actually been deployed.** Every claim above comes from `what-if`, which validates
against ARM but does not create resources. The template is proven to be *valid and plannable*, not
proven to *run*.

---

## Reproducing

```bash
cd Day23
./scripts/whatif.sh dev     # read-only, safe against anything
./scripts/whatif.sh prod

./scripts/deploy.sh dev     # plans, shows it, and refuses without explicit confirmation
```

`whatif.sh` compiles the template and the parameter file first and checks each exit status
directly — a syntax error caught locally costs two seconds; the same error caught by ARM costs a
round trip and an error naming a line in generated JSON rather than in the Bicep.
