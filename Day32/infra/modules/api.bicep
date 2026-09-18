// =================================================================================================
// The Dispatch API, as a Container App.
//
// The `secrets` array is empty, and that is the point of the file rather than an oversight. There
// is nothing to put in it: the registry is reached with a managed identity, the database is
// reached with the same identity, and the application has no third-party credential of its own.
// =================================================================================================

param name string
param location string

@description('Resource id of an EXISTING managed environment. This template never creates one — see main.bicep.')
param managedEnvironmentId string

param containerRegistryName string

@description('Fully-qualified image reference, tag pinned.')
param image string

@description('Resource id of the user-assigned identity. What the app is RUN AS.')
param identityResourceId string

@description('Client id of that same identity. What the SQL driver puts on the wire. Not interchangeable with the principal id.')
param identityClientId string

param sqlServerFqdn string
param sqlDatabaseName string
param tags object

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  tags: tags

  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityResourceId}': {}
    }
  }

  properties: {
    managedEnvironmentId: managedEnvironmentId

    configuration: {
      // No secrets. Not an empty placeholder for secrets that have not been added yet — there
      // are none to add.
      secrets: []

      ingress: {
        // Public. Dispatch has no authentication yet (Day 31 said so out loud and left it for
        // build-plan day 8), so "public" here means genuinely open, and the rate limiter and the
        // 64 KB body cap from Day 31 are the only things standing in front of it. Internal-only
        // ingress would be more honest about the auth gap, and would also make the thing
        // undemonstrable, which is the tradeoff this day is about.
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }

      registries: [
        {
          server: '${containerRegistryName}.azurecr.io'

          // Identity-based pull. The alternative is `username` + a `passwordSecretRef` pointing
          // at an ACR admin password, which is precisely the secret this whole design exists to
          // avoid — and which would have to live in the secrets array above.
          identity: identityResourceId
        }
      ]
    }

    template: {
      containers: [
        {
          name: 'dispatch-api'

          // Pinned to a tag, and `latest` is deliberately not used. `latest` makes the running
          // revision depend on when it last restarted rather than on what was deployed, so two
          // replicas of "the same" revision can be different builds and a rollback has nothing
          // to roll back to.
          image: image

          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }

          env: [
            {
              name: 'ConnectionStrings__Dispatch'

              // Readable in the portal by anyone with access, and safe to be. There is no
              // password in it. `Authentication=Active Directory Managed Identity` tells the
              // driver to fetch a token from the platform; `User Id` names WHICH identity, by
              // client id, because a container app may carry several.
              value: join([
                'Server=tcp:${sqlServerFqdn},1433'
                'Initial Catalog=${sqlDatabaseName}'
                'Encrypt=True'
                'TrustServerCertificate=False'
                'Connection Timeout=60'
                'Authentication=Active Directory Managed Identity'
                'User Id=${identityClientId}'
              ], ';')
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              // Off, explicitly, rather than left unset.
              //
              // Program.cs supports migrating at startup and defaults to not doing it, for
              // reasons it states: several replicas booting together race, and a destructive
              // migration would be applied by whichever one won with no human in the loop.
              // Setting it to false here makes the decision visible in the portal instead of
              // being a default somebody has to go and read the source to discover.
              //
              // Migrations run as a deployment step. See scripts/migrate.sh.
              name: 'DISPATCH_MIGRATE_ON_STARTUP'
              value: 'false'
            }
          ]

          probes: [
            {
              // Readiness, not liveness. The distinction matters during a cold database start:
              // a failing readiness probe takes the replica out of rotation until it recovers,
              // where a failing liveness probe would kill and restart it — turning a slow
              // database resume into a crash loop that never gets far enough to finish resuming.
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 10
              failureThreshold: 6
            }
          ]
        }
      ]

      scale: {
        // Zero. An idle Dispatch costs nothing, and that is the only reason this is affordable
        // to leave running on a student subscription alongside four other deployments.
        //
        // What it costs instead: the first request after an idle period pays a container cold
        // start AND a serverless database resume, back to back. Thirty to sixty seconds, and it
        // looks exactly like an outage. A floor of one replica would fix it for roughly Rs 57 a
        // day, which is more than everything else in this subscription combined currently costs.
        // The cheap fix is a keep-warm ping; the honest fix is to pay for the replica when
        // somebody is relying on the response time. Neither is done here, on purpose.
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
    }
  }
}

output name string = app.name
output url string = 'https://${app.properties.configuration.ingress.fqdn}'
output fqdn string = app.properties.configuration.ingress.fqdn
