// =============================================================================================
// Dispatch — the deployment-stack entry point.
//
// Day 23 produced a resource-group-scoped template that planned cleanly and was never deployed.
// Day 24 deploys it, and changes exactly one thing to do so: the entry point moves up to
// SUBSCRIPTION scope so the resource group itself becomes part of what is managed.
//
// Day 23's composition root is now `resources.bicep`, byte-for-byte the same design — same
// modules, same naming rules, same no-secrets property. Nothing about it needed to change to be
// deployable, which is the point worth noticing.
//
// ---------------------------------------------------------------------------------------------
// WHY SUBSCRIPTION SCOPE IS NOT A DETAIL
//
// A resource-group-scoped stack can manage everything IN a resource group but never the group.
// Tear it down and an empty resource group is left behind, along with anything a human dropped
// into it. Two of those are sitting in this subscription right now — `rg-dispatch-dev` and
// `rg-dispatch-prod`, created by Day 23's what-if runs and never cleaned up, because what-if
// needed a group to compare against and nothing owned it afterwards.
//
// Moving up one scope makes the group a managed resource like any other, so `actionOnUnmanage`
// applies to it too and teardown leaves nothing at all. That is the difference between "the
// resources are gone" and "the environment is gone".
// =============================================================================================

targetScope = 'subscription'

@description('Short environment name. Drives resource names, and is the azd environment name.')
@allowed([ 'dev', 'prod' ])
param environmentName string

@description('''
Deployment region.

Constrained to five because this subscription carries a `sys.regionrestriction` policy that
permits only those. Encoding the allow-list turns a runtime `RequestDisallowedByAzure` into a
parameter-validation error before anything is submitted.
''')
@allowed([
  'centralindia'
  'indonesiacentral'
  'malaysiawest'
  'uaenorth'
  'koreacentral'
])
param location string = 'centralindia'

@description('Object id of the Entra group that administers SQL. A group, not a person.')
param sqlAdminObjectId string

@description('Display name for that group.')
param sqlAdminLogin string

@description('Resource id of an existing Container Apps environment to reuse. Empty = create one.')
param existingManagedEnvironmentId string = ''

@description('Container image for the API.')
param containerImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

// ---- per-environment sizing, supplied whole from a committed profile ------------------------

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
// The resource group — a MANAGED resource, not a prerequisite.
//
// Day 23's scripts ran `az group create` before every plan. That group was owned by nothing: no
// template described it, no teardown removed it, and it outlived the thing it was created for.
// Declaring it here puts it inside the stack's inventory, so it is created by the same command
// that creates everything else and removed by the same command that removes everything else.
// ---------------------------------------------------------------------------------------------
resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-dispatch-${environmentName}'
  location: location
  tags: {
    application: 'dispatch'
    environment: environmentName
    managedBy: 'deployment-stack'

    // azd stamps this on everything it owns and uses it to find the environment again on the
    // next command. Without it `azd down` and `azd env refresh` cannot locate what they manage.
    'azd-env-name': environmentName
  }
}

module resources 'resources.bicep' = {
  name: 'dispatch-resources'
  scope: resourceGroup
  params: {
    environmentName: environmentName
    location: location
    sqlAdminObjectId: sqlAdminObjectId
    sqlAdminLogin: sqlAdminLogin
    existingManagedEnvironmentId: existingManagedEnvironmentId
    containerImage: containerImage
    databaseSku: databaseSku
    backupRetentionDays: backupRetentionDays
    sqlZoneRedundant: sqlZoneRedundant
    serviceBusSku: serviceBusSku
    serviceBusCapacity: serviceBusCapacity
    maxDeliveryCount: maxDeliveryCount
    messageTimeToLive: messageTimeToLive
    containerResources: containerResources
    minReplicas: minReplicas
    maxReplicas: maxReplicas
    logRetentionDays: logRetentionDays
    logDailyQuotaGb: logDailyQuotaGb
  }
}

// ---------------------------------------------------------------------------------------------
// Outputs.
//
// azd writes every one of these into the environment's .env file, where the next command — and
// any hook script — can read them by name. That is why the connection string is emitted as a
// plain output and remains safe: it ends `Authentication=Active Directory Default`, so it names
// an endpoint and an auth METHOD, and carries no credential to leak.
// ---------------------------------------------------------------------------------------------

output AZURE_RESOURCE_GROUP string = resourceGroup.name
output AZURE_LOCATION string = location

output API_URL string = resources.outputs.apiUrl
output API_PRINCIPAL_ID string = resources.outputs.apiPrincipalId

output SQL_SERVER_FQDN string = resources.outputs.sqlServerFqdn
output SQL_DATABASE_NAME string = resources.outputs.sqlDatabaseName
output SQL_CONNECTION_STRING string = resources.outputs.sqlConnectionString

output SERVICE_BUS_NAMESPACE string = resources.outputs.serviceBusNamespace
output SERVICE_BUS_TOPIC string = resources.outputs.serviceBusTopic

@description('Run this against the database, signed in as the SQL admin group, to finish the wiring.')
output GRANT_DATABASE_ACCESS_SCRIPT string = resources.outputs.grantDatabaseAccessScript
