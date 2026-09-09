// =============================================================================================
// Log Analytics + Application Insights.
//
// Present for one identity-specific reason beyond general telemetry: when a managed-identity
// token acquisition fails, the App Service log is where the real error appears. The HTTP
// response the caller sees is a generic 500, and the useful message — which resource was asked
// for, which identity was used, whether the token endpoint answered at all — is only in the
// application trace.
//
// The App Insights CONNECTION STRING is worth a note, because it looks like a counter-example to
// this whole exercise. It contains an InstrumentationKey, and it is passed to the app as a plain
// setting. That is Microsoft's documented model: the key is an ingestion identifier, it grants
// write-only access to a telemetry stream, and it cannot be used to READ anything. Entra
// authentication for App Insights ingestion does exist and would remove even that; it is a real
// remaining gap and EXERCISE.md records it as one rather than glossing over it.
// =============================================================================================

param location string
param workspaceName string
param tags object

@description('Retention, days. 30 is the Log Analytics floor.')
param retentionDays int = 30

@description('Daily ingestion cap, GB. Turns a logging bug into "ingestion stopped" rather than an invoice.')
param dailyQuotaGb int = 1

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
  name: replace(workspaceName, 'log-', 'appi-')
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id

    // Local auth left enabled. Disabling it is the setting that would remove the last
    // shared-key-shaped value from app settings, and it requires the ingestion endpoint to
    // accept Entra tokens from the app's identity. Named in EXERCISE.md as unfinished.
    DisableLocalAuth: false
  }
}

output workspaceId string = workspace.id
output connectionString string = appInsights.properties.ConnectionString
output appInsightsName string = appInsights.name
