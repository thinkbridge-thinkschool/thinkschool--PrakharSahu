// =============================================================================================
// The Dispatch API — a Container App with a system-assigned managed identity.
//
// Same hosting choice Day 17 arrived at, and for the same reason: a Container App gets a
// platform-issued identity at no extra cost, which is what lets everything downstream refuse
// stored credentials. Nothing in this module has a secret, and the `secrets` array is empty on
// purpose rather than by omission.
// =============================================================================================

@allowed([
  'centralindia'
  'indonesiacentral'
  'malaysiawest'
  'uaenorth'
  'koreacentral'
])
param location string

@allowed([ 'dev', 'prod' ])
param environmentName string

param nameSuffix string

@description('''
Log Analytics workspace NAME, not its resource id.

A name is computable before deployment; an id taken from another module's output is not, and
passing one here is what makes `what-if` short-circuit this entire module with
`NestedDeploymentShortCircuited`. The id is derived below with resourceId(), which resolves at
compile time.
''')
param logAnalyticsWorkspaceName string

@description('Container image. Public by default so a first deploy works before any registry exists.')
param containerImage string

@description('CPU cores and memory. Must be one of the supported pairs — 0.25/0.5Gi, 0.5/1Gi, 1/2Gi …')
param containerResources object

@description('Replica floor. 0 lets dev scale to zero and cost nothing idle; prod must never do that.')
@minValue(0)
@maxValue(10)
param minReplicas int

@minValue(1)
@maxValue(30)
param maxReplicas int

@description('Connection string for the Dispatch database. Carries NO password — see sql.bicep.')
param sqlConnectionString string

@description('Fully-qualified Service Bus namespace. No SAS key; access is by RBAC.')
param serviceBusNamespace string

param serviceBusTopic string

@description('''
Application Insights component NAME, for the same reason as the workspace above.

The connection string embeds an instrumentation key Azure generates, so it genuinely cannot be
computed before deployment. Resolving the component by name with `existing` keeps that unknown
INSIDE the module — where what-if renders it as a null property — instead of turning it into an
unevaluated module parameter, which stops the whole module being planned.
''')
param appInsightsName string

@description('''
Resource id of an EXISTING Container Apps environment to reuse. Empty means create one.

Azure permits ONE managed environment per region per subscription, and Day 17 discovered that by
hitting `MaxNumberOfRegionalEnvironmentsInSubExceeded` mid-deployment. An environment is a shared
boundary, not a per-project resource, so reusing one is the normal case and creating one is the
greenfield exception — which is why the parameter defaults to creating and the dev file overrides
it to reuse.
''')
param existingManagedEnvironmentId string = ''

param tags object

var createEnvironment = empty(existingManagedEnvironmentId)

// ---------------------------------------------------------------------------------------------
// The managed environment.
//
// One per region per subscription is the practical limit, and Day 17 hit it:
// `MaxNumberOfRegionalEnvironmentsInSubExceeded`. An environment is a shared boundary, not a
// per-project resource — so a real deployment usually LOOKS UP an existing one rather than
// creating its own. It is created here because Day 23 is describing a greenfield stack, and the
// alternative (an `existing` reference) would make the template undeployable on a clean
// subscription.
// ---------------------------------------------------------------------------------------------
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: logAnalyticsWorkspaceName
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = if (createEnvironment) {
  name: 'cae-dispatch-${environmentName}-${nameSuffix}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        // `existing` rather than reference()/listKeys() over a passed-in id. Same result, but the
        // resource is resolved by name at compile time, so the module stays evaluable.
        customerId: workspace.properties.customerId
        sharedKey: workspace.listKeys().primarySharedKey
      }
    }
    zoneRedundant: false
  }
}

// ---------------------------------------------------------------------------------------------
// The app itself.
// ---------------------------------------------------------------------------------------------
resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-dispatch-api-${environmentName}'
  location: location
  tags: tags

  // The whole point. A system-assigned identity is created with the app, dies with the app, and
  // cannot be copied anywhere. main.bicep grants it SQL and Service Bus access by object id.
  identity: {
    type: 'SystemAssigned'
  }

  properties: {
    managedEnvironmentId: createEnvironment ? environment.id : existingManagedEnvironmentId
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }

      // Empty, and that is the deliverable. Every other template of this shape has a SQL
      // password and a Service Bus connection string in here, referenced from Key Vault to make
      // them look safe. There is nothing to reference because there is nothing to store.
      secrets: []

      activeRevisionsMode: 'Single'
    }

    template: {
      containers: [
        {
          name: 'dispatch-api'
          image: containerImage
          resources: containerResources

          env: [
            // Identifiers and endpoints only. Nothing below is a credential; every one of them
            // could be printed in a public log without consequence.
            {
              name: 'ConnectionStrings__Dispatch'
              value: sqlConnectionString
            }
            {
              name: 'ServiceBus__FullyQualifiedNamespace'
              value: serviceBusNamespace
            }
            {
              name: 'ServiceBus__TopicName'
              value: serviceBusTopic
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsights.properties.ConnectionString
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: environmentName == 'prod' ? 'Production' : 'Development'
            }
          ]

          probes: [
            {
              // Liveness only. A readiness probe against /health would be wrong here: the app
              // answers /health without touching SQL deliberately, so that a database blip
              // degrades one feature rather than removing the whole revision from rotation.
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 10
              periodSeconds: 30
              failureThreshold: 3
            }
          ]
        }
      ]

      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-concurrency'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
}

output apiName string = api.name
output apiFqdn string = api.properties.configuration.ingress.fqdn
output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'

// The object id everything downstream grants access to. This output is the seam that lets
// main.bicep wire RBAC without this module knowing what it will be granted.
output principalId string = api.identity.principalId
output environmentId string = createEnvironment ? environment.id : existingManagedEnvironmentId
