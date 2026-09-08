// =============================================================================================
// Log Analytics, and the Application Insights workspace on top of it.
//
// Not one of the three the exercise names, and here anyway for a concrete reason: a Container
// Apps managed environment REQUIRES a Log Analytics workspace at creation. Leaving it out does
// not produce a smaller template, it produces a template that cannot deploy.
//
// It also closes a gap Day 22 piece 1 recorded honestly: "No metrics export. The event log and
// counters are in-process and readable over HTTP; nothing ships them to a dashboard, so the
// breaker's state is observable only if somebody asks."
// =============================================================================================

@allowed([
  'centralindia'
  'indonesiacentral'
  'malaysiawest'
  'uaenorth'
  'koreacentral'
])
param location string

@description('Workspace name, computed by the caller.')
param workspaceName string

@description('Application Insights component name, computed by the caller.')
param appInsightsName string

@description('How long telemetry is kept. The single biggest driver of cost in this module.')
@minValue(30)
@maxValue(730)
param retentionDays int

@description('Daily ingestion cap in GB. -1 means uncapped. A cap is a blast radius, not a budget.')
param dailyQuotaGb int

param tags object

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionDays

    // A runaway logger is the classic way to discover that observability has no upper bound on
    // cost. The cap makes the failure "we stopped ingesting" rather than a surprise invoice —
    // and dev gets a much tighter one, because nothing in dev is worth a large bill.
    workspaceCapping: {
      dailyQuotaGb: dailyQuotaGb
    }

    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// Workspace-based Application Insights. The classic, non-workspace kind is retired; a component
// created without `WorkspaceResourceId` today is a component that stops being supported.
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

output workspaceId string = workspace.id
output workspaceCustomerId string = workspace.properties.customerId
output appInsightsConnectionString string = appInsights.properties.ConnectionString
