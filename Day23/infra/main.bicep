// =============================================================================================
// Dispatch — infrastructure for the Day 22 piece 2 capstone.
//
// Three modules the exercise names (API, SQL, Service Bus), one it does not (observability,
// because a Container Apps environment cannot be created without a Log Analytics workspace),
// and the role assignments that join them.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE IS FOR, beyond "the resources exist"
//
// Every capability below closes a gap the capstone recorded against itself:
//
//   Day 22 p2: "No persistence. All three stores are dictionaries."        -> sql.bicep
//   Day 22 p2: "Replace InProcessIntegrationEventPublisher with a topic."  -> servicebus.bicep
//   Day 22 p1: "No metrics export ... observable only if somebody asks."   -> observability.bicep
//
// ---------------------------------------------------------------------------------------------
// THE ONE PROPERTY WORTH DEFENDING
//
// There are no secrets in this deployment. Not stored, not referenced from Key Vault, not passed
// as secure parameters — none exist to store. SQL is Entra-only, Service Bus has local auth
// disabled, and the API reaches both with its system-assigned managed identity.
//
// `az deployment group what-if` on this template shows zero `Microsoft.KeyVault` resources and
// zero `secureString` parameters, which is a stronger claim than "we keep our secrets safe".
// =============================================================================================

targetScope = 'resourceGroup'

// ---------------------------------------------------------------------------------------------
// Parameters. Everything that differs between environments arrives here — nothing branches on
// `environmentName` to decide a SIZE, only to decide a NAME.
// ---------------------------------------------------------------------------------------------

@description('Short environment name. Drives resource names and nothing else.')
@allowed([ 'dev', 'prod' ])
param environmentName string

@description('''
Deployment region.

Constrained to five because this subscription carries a `sys.regionrestriction` policy that
permits only those, and Day 17 spent a deployment discovering it the hard way. Encoding the
allow-list here turns a runtime `RequestDisallowedByAzure` into a parameter-validation error
before anything is submitted.
''')
@allowed([
  'centralindia'
  'indonesiacentral'
  'malaysiawest'
  'uaenorth'
  'koreacentral'
])
param location string = 'centralindia'

@description('Object id of the Entra group that administers SQL. A group, not a person — people leave.')
param sqlAdminObjectId string

@description('Display name for that group.')
param sqlAdminLogin string

@description('Resource id of an existing Container Apps environment to reuse. Empty = create one.')
param existingManagedEnvironmentId string = ''

@description('Container image for the API.')
param containerImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

// ---- per-environment sizing -----------------------------------------------------------------

@description('SQL database SKU.')
param databaseSku object

@description('SQL backup retention, days.')
param backupRetentionDays int

@description('SQL zone redundancy.')
param sqlZoneRedundant bool

@description('Service Bus namespace SKU. Standard is the minimum that supports topics.')
@allowed([ 'Standard', 'Premium' ])
param serviceBusSku string

@description('Premium messaging units. Ignored on Standard.')
param serviceBusCapacity int = 1

@description('Deliveries before the broker dead-letters a message itself.')
param maxDeliveryCount int

@description('Message time-to-live, ISO 8601.')
param messageTimeToLive string

@description('API container CPU and memory.')
param containerResources object

@description('Replica floor. 0 lets an idle environment cost nothing.')
param minReplicas int

@description('Replica ceiling.')
param maxReplicas int

@description('Telemetry retention, days.')
param logRetentionDays int

@description('Daily ingestion cap, GB.')
param logDailyQuotaGb int

// ---------------------------------------------------------------------------------------------
// Naming.
//
// SQL servers, Service Bus namespaces and storage accounts share a GLOBAL namespace, so a name
// that only encodes the environment collides the moment somebody else deploys the same template.
// Deriving the suffix from the resource group id makes it unique, stable across redeploys, and
// impossible to typo — three properties a hand-typed suffix has none of.
// ---------------------------------------------------------------------------------------------
var nameSuffix = take(uniqueString(resourceGroup().id), 6)

// Every resource name lives HERE, in the composition root, not inside the module that creates it.
//
// This is not tidiness. A name built inside a module is unknown until that module runs, so any
// value derived from it — a resource id, an FQDN, a connection string — is also unknown, and
// `what-if` reports `NestedDeploymentShortCircuited` and validates nothing downstream. Naming
// centrally keeps the whole graph resolvable before a single resource is submitted, which is the
// difference between a plan you can review and a plan that says "12 to create" and stays quiet
// about the rest.
var sqlServerName = 'sql-dispatch-${environmentName}-${nameSuffix}'
var serviceBusNamespaceName = 'sb-dispatch-${environmentName}-${nameSuffix}'
var workspaceName = 'log-dispatch-${environmentName}-${nameSuffix}'
var appInsightsName = 'appi-dispatch-${environmentName}-${nameSuffix}'

// Derived endpoints. Both follow documented, stable Azure naming, so neither needs a module
// output — and not needing one is exactly what keeps the api module evaluable at plan time.
var sqlServerFqdn = '${sqlServerName}${environment().suffixes.sqlServerHostname}'
var serviceBusFqdn = '${serviceBusNamespaceName}.servicebus.windows.net'

var sqlConnectionString = join([
  'Server=tcp:${sqlServerFqdn},1433'
  'Initial Catalog=dispatch'
  'Encrypt=True'
  'TrustServerCertificate=False'
  'Connection Timeout=30'
  'Authentication=Active Directory Default'
], ';')

var tags = {
  application: 'dispatch'
  environment: environmentName
  managedBy: 'bicep'
  // Deliberately no `deployedOn` timestamp. utcNow() cannot be used outside a parameter default,
  // and baking a time into tags makes every what-if report a change on every resource.
  source: 'Day23/infra/main.bicep'
}

// ---------------------------------------------------------------------------------------------
// Modules.
// ---------------------------------------------------------------------------------------------

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
    existingManagedEnvironmentId: existingManagedEnvironmentId
    containerImage: containerImage
    containerResources: containerResources
    minReplicas: minReplicas
    maxReplicas: maxReplicas

    // Both of these are endpoints, not credentials. See the module headers.
    sqlConnectionString: sqlConnectionString
    serviceBusNamespace: serviceBusFqdn
    serviceBusTopic: 'work-order-events'

    appInsightsName: appInsightsName
    tags: tags
  }
}

// ---------------------------------------------------------------------------------------------
// RBAC — the part that replaces every connection string this stack does not have.
//
// The API publishes work-order events and consumes them on two subscriptions, so it needs BOTH
// sender and receiver. Two separate assignments rather than the broader `Azure Service Bus Data
// Owner`, because Owner also grants manage rights the application has no use for — and a role
// that grants more than the workload needs is the same mistake as an over-scoped secret.
//
// Role ids are the well-known built-in GUIDs. They are hard-coded because they are stable
// platform constants, not configuration; looking them up at deploy time would add a failure mode
// to save nothing.
// ---------------------------------------------------------------------------------------------
module rbac 'modules/rbac.bicep' = {
  name: 'rbac'
  params: {
    serviceBusNamespaceName: serviceBusNamespaceName
    principalId: api.outputs.principalId
  }
}

// ---------------------------------------------------------------------------------------------
// Outputs.
//
// The SQL side has a step Bicep cannot perform, and pretending otherwise would be the dishonest
// part of this template. Granting the managed identity a database user is a T-SQL statement
// (`CREATE USER [<app>] FROM EXTERNAL PROVIDER`), executed INSIDE the database — there is no ARM
// resource for it. The command is emitted here so the deploy script can run it, rather than
// being left as tribal knowledge.
// ---------------------------------------------------------------------------------------------

output apiUrl string = api.outputs.apiUrl
output apiPrincipalId string = api.outputs.principalId

output sqlServerFqdn string = sqlServerFqdn
output sqlDatabaseName string = sql.outputs.databaseName

output serviceBusNamespace string = serviceBusFqdn
output serviceBusTopic string = serviceBus.outputs.topicName
output serviceBusSubscriptions array = serviceBus.outputs.subscriptionNames

@description('Run this against the database, signed in as the SQL admin group, to finish the wiring.')
output grantDatabaseAccessScript string = join([
  'CREATE USER [${api.outputs.apiName}] FROM EXTERNAL PROVIDER;'
  'ALTER ROLE db_datareader ADD MEMBER [${api.outputs.apiName}];'
  'ALTER ROLE db_datawriter ADD MEMBER [${api.outputs.apiName}];'
  'ALTER ROLE db_ddladmin  ADD MEMBER [${api.outputs.apiName}];'
], ' ')
