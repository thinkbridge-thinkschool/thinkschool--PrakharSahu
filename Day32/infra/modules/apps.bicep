// =================================================================================================
// The four applications.
//
//   ca-quotes-web     Angular, served by nginx. Talks only to the broker.
//   ca-quotes-bff     the broker. Holds the identity, mints the caller token, proxies to the API.
//   ca-quotes-api     the Day 27 modular monolith. Refuses anything without a valid caller token.
//   ca-dispatch-api   the Day 31 capstone. Independent of the other three.
//
// One file rather than four, because the interesting content is what they SHARE — one identity, one
// registry, one SQL server, one set of decisions about scale and secrets — and four near-identical
// files would hide that behind copy-paste.
//
// Between the browser and the database there is exactly one secret reference (the JWT signing key)
// and one credential that is minted per request and never stored (the managed-identity token). No
// app here has a password, a connection key, or a client secret.
// =================================================================================================

param location string
param managedEnvironmentId string
param containerRegistryName string
param imageTag string

@description('Resource id of the shared user-assigned identity. What every app is RUN AS.')
param identityResourceId string

@description('Client id of that same identity. What the SQL driver puts on the wire. Not interchangeable with the principal id.')
param identityClientId string

param quotesApiName string
param quotesBffName string
param quotesWebName string
param dispatchApiName string

param quotesApiUrl string
param quotesBffUrl string
param quotesWebUrl string

param quotesApiAppIdUri string
param quotesApiTenantId string
param quotesApiRequiredRole string

@description('Tenant whose tokens Dispatch accepts. Empty disables authentication entirely -- see DispatchAuth for why that is the local and test configuration and never the deployed one.')
param dispatchAuthTenantId string = ''

@description('Audience Dispatch requires, i.e. api://<appId> of its own app registration. Its OWN, not the Quotes API one: an audience is the answer to "which API is this token for", and sharing one makes the check a formality.')
param dispatchAuthAudience string = ''

param sqlServerFqdn string

@description('Redis endpoint as host:port, with no credential. The client fetches a token for it — see modules/redis.bicep.')
param redisEndpoint string

@description('Application Insights connection string, or empty. Not a secret: it carries an ingestion key that can only WRITE telemetry, and Azure prints it in the portal. Treating it as a secret would hide a value that is not sensitive and dilute the signal that the two in Key Vault are.')
param appInsightsConnectionString string = ''

@description('Versionless Key Vault URI of the JWT signing key.')
param jwtSecretUri string

@description('Fully-qualified Service Bus namespace, e.g. ns.servicebus.windows.net. A hostname, not a credential: the API authenticates to it with the managed identity, because the namespace has SAS disabled.')
param serviceBusFullyQualifiedNamespace string

param tags object

var registryServer = '${containerRegistryName}.azurecr.io'

// Identity-based registry auth. The alternative is a username plus a passwordSecretRef pointing at
// an ACR admin password — precisely the secret this whole design exists to avoid, and one that
// would have to be stored in each app's secrets array.
var registries = [
  {
    server: registryServer
    identity: identityResourceId
  }
]

var userAssigned = {
  type: 'UserAssigned'
  userAssignedIdentities: {
    '${identityResourceId}': {}
  }
}

// A connection string with no password in it, and safe to read in the portal for that reason.
// `Authentication=Active Directory Managed Identity` tells the driver to fetch a token from the
// platform; `User Id` names WHICH identity by CLIENT id, because a container app may carry several
// and the driver will not guess.
func sqlConnectionString(serverFqdn string, database string, clientId string) string => join([
  'Server=tcp:${serverFqdn},1433'
  'Initial Catalog=${database}'
  'Encrypt=True'
  'TrustServerCertificate=False'
  'Connection Timeout=60'
  'Authentication=Active Directory Managed Identity'
  'User Id=${clientId}'
], ';')

// Scale to zero, everywhere.
//
// An idle stack costs nothing, which is the only reason four applications are affordable to leave
// running here. What it costs instead: the first request after an idle period pays a container cold
// start AND a serverless database resume, back to back. Thirty to sixty seconds, and it looks
// exactly like an outage.
//
// A floor of one replica would fix it for roughly Rs 57 per app per day — more, for one app, than
// everything else in this subscription currently costs together. The cheap mitigation is a
// keep-warm ping; the honest one is to pay for the replica the moment somebody depends on the
// response time. Neither is done here, on purpose, and it is the first thing the postmortem says
// it would change.
var scaleToZero = {
  minReplicas: 0
  maxReplicas: 3
  rules: [
    {
      name: 'http-concurrency'
      http: {
        metadata: {
          concurrentRequests: '40'
        }
      }
    }
  ]
}

// Readiness, not liveness, and the distinction is load-bearing during a cold database start. A
// failing readiness probe takes the replica out of rotation until it recovers; a failing liveness
// probe kills and restarts it, turning a slow database resume into a crash loop that never survives
// long enough to finish resuming.
func readiness(path string) array => [
  {
    type: 'Readiness'
    httpGet: {
      path: path
      port: 8080
    }
    initialDelaySeconds: 5
    periodSeconds: 10
    failureThreshold: 6
  }
]

// -------------------------------------------------------------------------------------------------
// The Quotes API — the Day 27 modular monolith. Five modules, twenty-three projects, one process.
// -------------------------------------------------------------------------------------------------
resource quotesApi 'Microsoft.App/containerApps@2024-03-01' = {
  name: quotesApiName
  location: location
  tags: tags
  identity: userAssigned
  properties: {
    managedEnvironmentId: managedEnvironmentId
    configuration: {
      // One Key Vault reference. The value never appears in this template, in the deployment
      // history, or in `az containerapp show` — the platform resolves it at container start using
      // the identity named below.
      secrets: [
        {
          name: 'jwt-signing-key'
          keyVaultUrl: jwtSecretUri
          identity: identityResourceId
        }
      ]
      ingress: {
        // Public, and it refuses direct calls anyway — that is the Day 17 design and it is the
        // single best thing in this deployment to demonstrate. Every /api/* request must carry an
        // X-Caller-Token that is an app-only Entra token for the audience below, carrying the role
        // below. The browser cannot mint one. Only the broker can.
        //
        // Internal-only ingress was the alternative and would be defence in depth. It was not
        // taken because this environment is shared with four other applications from other days,
        // so "internal" is a weaker boundary than it sounds, and because a boundary nobody can see
        // working is a boundary nobody maintains.
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: registries
    }
    template: {
      containers: [
        {
          name: 'quotes-api'
          // Pinned to a tag; `latest` is deliberately not used. `latest` makes the running build
          // depend on when a replica last restarted rather than on what was deployed, so two
          // replicas of "the same" revision can be different code and a rollback has nothing to
          // roll back to.
          image: '${registryServer}/quotes-api-day32:${imageTag}'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'ConnectionStrings__DefaultConnection'
              value: sqlConnectionString(sqlServerFqdn, 'quotes', identityClientId)
            }
            {
              name: 'Jwt__Key'
              secretRef: 'jwt-signing-key'
            }
            {
              // Host and port. No password, no access key, no connection string with a secret in
              // it — the client asks the platform for a token, exactly as the SQL driver does.
              // AZURE_CLIENT_ID below is what tells it which identity to present.
              //
              // Without an L2 this stack has only HybridCache's per-process L1, which with
              // minReplicas: 0 is empty almost every time it is consulted.
              name: 'ConnectionStrings__Redis'
              value: redisEndpoint
            }
            {
              // A NAMESPACE, not a connection string, and that is the whole point.
              //
              // The first version of this deployment passed a SAS connection string from Key Vault.
              // Every consumer failed on a loop with "LocalAuthDisabled: SAS authentication has
              // been disabled for the namespace" — Day 26 turned local auth off on purpose, and
              // MessagingExtensions.cs documents it. The credential was stored perfectly and was
              // worth nothing.
              //
              // With a namespace and no credential, the client asks the platform for a token, the
              // same way the SQL driver does. ServiceBusOptions.Enabled is true when EITHER this
              // or a connection string is set, so messaging is live: the outbox relay, the two
              // competing consumers and the dead-letter endpoints all work.
              name: 'ServiceBus__FullyQualifiedNamespace'
              value: serviceBusFullyQualifiedNamespace
            }
            {
              // Which identity DefaultAzureCredential should ask IMDS for. A container app may
              // carry several and the credential chain will not guess; without this it requests a
              // token for the system-assigned identity, which this app does not have.
              name: 'AZURE_CLIENT_ID'
              value: identityClientId
            }
            {
              // Public identifiers, all three. A tenant id, an application id URI and a role name
              // are printed in the portal and mean nothing without a token. Marking them secret
              // would hide nothing and would dilute the signal that the two above are different.
              name: 'CallerIdentity__TenantId'
              value: quotesApiTenantId
            }
            {
              name: 'CallerIdentity__Audience'
              value: quotesApiAppIdUri
            }
            {
              name: 'CallerIdentity__RequiredRole'
              value: quotesApiRequiredRole
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              // Day 26's component, reused. A second one would split one system's traces across two
              // places, which is worse than having none: you would look in the wrong one and
              // conclude nothing was instrumented.
              //
              // Empty is a supported value -- Program.cs logs `azureMonitor=disabled` and carries on
              // -- so a deployment without observability still works rather than refusing to start.
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
            }
          ]
          probes: readiness('/health')
        }
      ]
      scale: scaleToZero
    }
  }
}

// -------------------------------------------------------------------------------------------------
// The broker.
//
// The only process in the Quotes stack that holds an identity capable of calling the API. It
// attaches TWO tokens to every upstream request and keeping them in separate headers is the whole
// design:
//
//   Authorization    the end user's own JWT, passed through untouched
//   X-Caller-Token   this tier's managed-identity token
//
// Putting the managed-identity token in Authorization was tried first and is more conventional. It
// collapses the two identities into one, so the API's ownership checks read `sub` from the managed
// identity and silently reassign every quote to it. Written up in Day17/VERIFICATION.md.
// -------------------------------------------------------------------------------------------------
resource quotesBff 'Microsoft.App/containerApps@2024-03-01' = {
  name: quotesBffName
  location: location
  tags: tags
  identity: userAssigned
  properties: {
    managedEnvironmentId: managedEnvironmentId
    configuration: {
      // Empty, and not a placeholder for secrets not yet added. The broker's only credential is
      // minted per request by the platform and never stored.
      secrets: []
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: registries
    }
    template: {
      containers: [
        {
          name: 'quotes-bff'
          image: '${registryServer}/quotes-bff-day32:${imageTag}'
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'UPSTREAM_API_BASE'
              value: quotesApiUrl
            }
            {
              name: 'API_APP_ID_URI'
              value: quotesApiAppIdUri
            }
            {
              // Never `*`. A browser refuses to send credentials to a wildcard origin, so a
              // wildcard here would break sign-in rather than loosen it — a case where the
              // permissive option is also the broken one.
              name: 'ALLOWED_ORIGINS'
              value: quotesWebUrl
            }
            {
              // Which identity to ask IMDS for. The broker runs under a user-assigned identity, and
              // DefaultAzureCredential cannot guess which one when an app carries several.
              name: 'AZURE_CLIENT_ID'
              value: identityClientId
            }
          ]
          probes: readiness('/healthz')
        }
      ]
      scale: scaleToZero
    }
  }
}

// -------------------------------------------------------------------------------------------------
// The Angular client, served by nginx.
//
// A stand-in for Azure Static Web Apps, which cannot be created in this subscription at all: SWA is
// offered in centralus, eastus2, westus2, westeurope and eastasia; the subscription's region policy
// permits only centralindia, indonesiacentral, malaysiawest, uaenorth and koreacentral; the two sets
// do not intersect. Day 17's DEPLOY.md still describes the Static Web App that was originally
// intended, including a custom-domain section for a domain that was never bound. That is a
// documentation bug, and this is the deployment that actually exists.
//
// What is lost by not having SWA: the free managed certificate (Container Apps provides one
// anyway), per-pull-request staging environments, and the custom-domain binding. Only the middle
// one would have been genuinely useful.
// -------------------------------------------------------------------------------------------------
resource quotesWeb 'Microsoft.App/containerApps@2024-03-01' = {
  name: quotesWebName
  location: location
  tags: tags
  identity: userAssigned
  properties: {
    managedEnvironmentId: managedEnvironmentId
    configuration: {
      secrets: []
      ingress: {
        external: true
        // nginx, not Kestrel: port 80 inside the container.
        targetPort: 80
        transport: 'auto'
        allowInsecure: false
      }
      registries: registries
    }
    template: {
      containers: [
        {
          name: 'quotes-web'
          image: '${registryServer}/quotes-web-day32:${imageTag}'
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              // Substituted into the Content-Security-Policy at container start by the nginx
              // image's envsubst step, so connect-src names the broker and nothing else. An
              // injected script has nowhere to exfiltrate to.
              //
              // Note what this does NOT do: the Angular bundle's own apiBaseUrl is baked in at
              // BUILD time by environment.production.ts. Changing this variable moves the CSP and
              // not the requests, which would produce a frontend that is allowed to call a host it
              // never calls. scripts/build-images.sh stamps both from the same computed value.
              name: 'BFF_ORIGIN'
              value: quotesBffUrl
            }
          ]
          probes: [
            {
              type: 'Readiness'
              httpGet: {
                path: '/'
                port: 80
              }
              initialDelaySeconds: 3
              periodSeconds: 10
              failureThreshold: 6
            }
          ]
        }
      ]
      scale: scaleToZero
    }
  }
}

// -------------------------------------------------------------------------------------------------
// Dispatch — the capstone, deployed for the first time.
//
// Independent of the three above: its own database, its own module set, no shared code beyond the
// fact that both are .NET. It has no authentication, which Day 31 said out loud and left for
// build-plan day 8; the rate limiter and the 64 KB body cap from that day are the only things in
// front of it.
// -------------------------------------------------------------------------------------------------
resource dispatchApi 'Microsoft.App/containerApps@2024-03-01' = {
  name: dispatchApiName
  location: location
  tags: tags
  identity: userAssigned
  properties: {
    managedEnvironmentId: managedEnvironmentId
    configuration: {
      secrets: []
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: registries
    }
    template: {
      containers: [
        {
          name: 'dispatch-api'
          image: '${registryServer}/dispatch-api:${imageTag}'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'ConnectionStrings__Dispatch'
              value: sqlConnectionString(sqlServerFqdn, 'dispatch', identityClientId)
            }
            {
              // Off, explicitly, rather than left unset. Program.cs supports migrating at startup
              // and defaults to not doing it, for reasons it states: several replicas booting
              // together race on the migration lock, and a destructive migration would be applied
              // by whichever replica won with no human in the loop. Setting it false here makes
              // the decision visible in the portal instead of something you have to read the
              // source to discover. Migrations run as a deployment step — scripts/migrate.sh.
              name: 'DISPATCH_MIGRATE_ON_STARTUP'
              value: 'false'
            }
            {
              // Day 31 shipped Dispatch with no authentication and said so out loud. These two
              // settings are what close it: with both present the API validates a bearer token
              // against Entra and enforces a role on every endpoint. Absent, the scheme is
              // disabled entirely — which is how twenty-six integration tests and one end-to-end
              // test boot this application without minting tokens they are not testing.
              name: 'Auth__TenantId'
              value: dispatchAuthTenantId
            }
            {
              // Dispatch's OWN audience, not the Quotes API's. An audience is the answer to
              // "which API is this token for"; sharing one would mean a token minted for Quotes
              // is accepted here, and the check stops being a boundary.
              name: 'Auth__Audience'
              value: dispatchAuthAudience
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
          ]
          probes: readiness('/health')
        }
      ]
      scale: scaleToZero
    }
  }
}

output quotesApiFqdn string = quotesApi.properties.configuration.ingress.fqdn
output quotesBffFqdn string = quotesBff.properties.configuration.ingress.fqdn
output quotesWebFqdn string = quotesWeb.properties.configuration.ingress.fqdn
output dispatchApiFqdn string = dispatchApi.properties.configuration.ingress.fqdn
