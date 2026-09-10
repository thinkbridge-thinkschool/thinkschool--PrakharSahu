// =============================================================================================
// Day 26 — the telemetry destination, and the broker the trace has to survive.
//
// Two things are provisioned here and they answer two different questions:
//
//   App Insights + Log Analytics   where the telemetry LANDS, and what KQL runs against
//   Service Bus topic              the hop that BREAKS a trace unless context is carried
//
// ---------------------------------------------------------------------------------------------
// WHY A BROKER IS PART OF AN OBSERVABILITY EXERCISE
//
// In-process tracing is close to free: .NET's Activity flows on the async context, so a span
// created inside a request is automatically the child of that request. Nothing has to be wired.
//
// A trace only becomes hard when it crosses a boundary the async context does not follow. This
// deployment creates one of them on purpose — a Service Bus topic — and the application creates
// a second, a database row. "Distributed tracing works" is only a meaningful claim about those.
// =============================================================================================

targetScope = 'resourceGroup'

@description('Short environment name. Drives resource names and nothing else.')
@allowed([ 'dev' ])
param environmentName string = 'dev'

@description('Deployment region. Constrained by the subscription regionrestriction policy.')
@allowed([
  'centralindia'
  'indonesiacentral'
  'malaysiawest'
  'uaenorth'
  'koreacentral'
])
param location string = 'centralindia'

@description('''
Object id of the principal that runs the application locally.

Day 22 piece 1 authenticated to Service Bus with a SAS connection string. That is replaced here
with DefaultAzureCredential, which means the signed-in `az login` identity needs the data roles —
the same model Day 25 established, applied to a process running on a laptop rather than in App
Service.
''')
param developerObjectId string

@description('Telemetry retention, days. 30 is the Log Analytics floor.')
param retentionDays int = 30

@description('Daily ingestion cap, GB. Turns a logging bug into stopped ingestion, not an invoice.')
param dailyQuotaGb int = 1

@description('Error-rate threshold, percent, above which the alert fires.')
param errorRateThresholdPercent int = 5

var nameSuffix = take(uniqueString(resourceGroup().id), 6)

var workspaceName = 'log-quotes-${environmentName}-${nameSuffix}'
var appInsightsName = 'appi-quotes-${environmentName}-${nameSuffix}'
var serviceBusName = 'sb-quotes-${environmentName}-${nameSuffix}'

var topicName = 'quote-events'
var auditSubscription = 'audit'
var searchSubscription = 'search-index'

var tags = {
  application: 'quotes-api'
  environment: environmentName
  managedBy: 'bicep'
  source: 'Day26/infra/main.bicep'
}

// ---------------------------------------------------------------------------------------------
// Log Analytics.
//
// App Insights is workspace-based, which is not a formality: `requests`, `dependencies` and
// `traces` are Log Analytics tables, and every KQL query in Day26/kql runs against this
// workspace. Classic (non-workspace) mode is retired and cannot be created.
// ---------------------------------------------------------------------------------------------
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionDays
    workspaceCapping: {
      dailyQuotaGb: dailyQuotaGb
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id

    // ---------------------------------------------------------------------------------------
    // SAMPLING IS PINNED TO 100%, deliberately rather than by omission.
    //
    // Adaptive sampling drops telemetry to control cost and is the correct default for a busy
    // production service. It is wrong here: a sampled-out span produces a trace with a HOLE in
    // it, and a hole is indistinguishable from a propagation bug. The entire deliverable is
    // "the trace stitches end to end", so the trace has to be complete or the evidence is
    // worthless.
    //
    // The daily cap above is the cost control instead. It stops ingestion rather than silently
    // thinning it — a limit that fails loudly instead of quietly.
    // ---------------------------------------------------------------------------------------
    SamplingPercentage: 100

    DisableLocalAuth: false
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// ---------------------------------------------------------------------------------------------
// Service Bus — the boundary the trace has to cross.
//
// `disableLocalAuth: true` for the reason Day 25 established: a SAS key the namespace refuses is
// worth nothing if it leaks. Day 22 piece 1 used a connection string, so the application change
// that accompanies this template is a switch to DefaultAzureCredential.
// ---------------------------------------------------------------------------------------------
resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: serviceBusName
  location: location
  tags: tags
  sku: {
    name: 'Standard'      // the minimum tier that supports topics
    tier: 'Standard'
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource topic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: namespace
  name: topicName
  properties: {
    defaultMessageTimeToLive: 'P1D'
    enablePartitioning: false
  }
}

// Two subscriptions, because a topic that only ever has one is a queue with extra steps. Both
// receive every quote.created event and neither knows the other exists — and in a trace both
// appear as separate children of the same publish span, which is the clearest possible picture
// of fan-out.
resource auditSub 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: topic
  name: auditSubscription
  properties: {
    maxDeliveryCount: 3
    lockDuration: 'PT30S'
    defaultMessageTimeToLive: 'P1D'
    deadLetteringOnMessageExpiration: true
  }
}

resource searchSub 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: topic
  name: searchSubscription
  properties: {
    maxDeliveryCount: 3
    lockDuration: 'PT30S'
    defaultMessageTimeToLive: 'P1D'
    deadLetteringOnMessageExpiration: true
  }
}

// ---------------------------------------------------------------------------------------------
// RBAC for the identity that runs the app locally.
//
// With local auth disabled there is no connection string to fall back on, so the process on a
// laptop authenticates as the signed-in `az login` user. Sender and Receiver separately rather
// than Data Owner, for the reason Day 25 recorded: Owner also grants manage rights the workload
// never uses.
// ---------------------------------------------------------------------------------------------
module rbac 'modules/rbac.bicep' = {
  name: 'rbac'
  params: {
    serviceBusNamespaceName: serviceBusName
    principalId: developerObjectId
  }
  dependsOn: [
    namespace
  ]
}

// ---------------------------------------------------------------------------------------------
// The error-rate alert, deployed as code rather than clicked together in the portal — an alert
// nobody can review is an alert nobody trusts.
// ---------------------------------------------------------------------------------------------
module alert 'modules/alert.bicep' = {
  name: 'alert'
  params: {
    location: location
    appInsightsId: appInsights.id
    thresholdPercent: errorRateThresholdPercent
    tags: tags
  }
}

output appInsightsName string = appInsights.name
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output workspaceId string = workspace.id
output workspaceCustomerId string = workspace.properties.customerId

output serviceBusFullyQualifiedNamespace string = '${namespace.name}.servicebus.windows.net'
output serviceBusTopic string = topic.name
output serviceBusAuditSubscription string = auditSub.name
output serviceBusSearchSubscription string = searchSub.name

output alertRuleName string = alert.outputs.ruleName
