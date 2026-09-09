// =============================================================================================
// App Service — where the whole claim is either true or false.
//
// Every other module removed a reason to hold a secret. This file is where the absence has to
// show: the app settings below are the exact list a reviewer would dump with
// `az webapp config appsettings list`, and not one of them contains a credential.
//
// ---------------------------------------------------------------------------------------------
// THE THREE KINDS OF VALUE IN `appSettings`, AND WHY ONLY ONE IS A SECRET
//
//   1. ENDPOINTS AND IDENTIFIERS — a hostname, a database name, a queue name, a client id.
//      Public by design. A client id is meant to be embedded in browser JavaScript. Treating
//      these as secrets is the cargo-cult version of security: it costs real effort and protects
//      nothing, and it dilutes the meaning of "secret" for the values that are.
//
//   2. THE SQL CONNECTION STRING — carries no credential, which is the point. It ends
//      `Authentication=Active Directory Managed Identity`, naming an auth METHOD rather than
//      supplying one. Printing it in a log reveals a hostname somebody still cannot connect to.
//
//   3. THE THIRD-PARTY KEY — genuinely a secret, and therefore NOT here. What is here is a
//      pointer: `@Microsoft.KeyVault(...)`. See the block on that below.
//
// ---------------------------------------------------------------------------------------------
// WHAT IS DELIBERATELY ABSENT
//
//   connectionStrings         the App Service slot built for connection strings is empty. It
//                             exists, it is the obvious place to paste a password, and nothing
//                             is in it.
//   clientSecretSettingName   Easy Auth supports a client secret. This configuration does not
//                             use one, because it validates tokens rather than issuing them.
//   any *_KEY / *_PASSWORD    no setting holds a credential value.
// =============================================================================================

param location string
param planName string
param siteName string
param tags object

param managedIdentityId string
param managedIdentityClientId string

param entraClientId string
param tenantId string

param sqlServerFqdn string
param sqlDatabaseName string
param serviceBusFqdn string
param serviceBusQueueName string

param keyVaultName string
param thirdPartySecretName string

param appInsightsConnectionString string

// ---------------------------------------------------------------------------------------------
// B1, Linux. The smallest tier that supports what this needs.
//
// The Free (F1) tier cannot run Always On, and without it the app is unloaded when idle. Key
// Vault references resolve at STARTUP, so an app that is constantly being unloaded and reloaded
// re-resolves them constantly — turning a one-time permission check into a recurring one and
// making an intermittent RBAC problem look like a flaky app.
// ---------------------------------------------------------------------------------------------
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  tags: tags
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

// ---------------------------------------------------------------------------------------------
// The SQL connection string. Read it closely — the absence is the feature.
//
// `Authentication=Active Directory Managed Identity` tells Microsoft.Data.SqlClient to fetch a
// token from the platform rather than to authenticate with anything supplied here.
//
// `User Id=<clientId>` is NOT a username. With a user-assigned identity it names WHICH identity
// to request a token for, because a resource can carry several and the token endpoint will not
// guess. This is the single most commonly missed line in a user-assigned setup, and omitting it
// produces "ManagedIdentityCredential authentication failed" — an error that reads like a
// permissions problem and is an ambiguity problem.
// ---------------------------------------------------------------------------------------------
var sqlConnectionString = join([
  'Server=tcp:${sqlServerFqdn},1433'
  'Initial Catalog=${sqlDatabaseName}'
  'Encrypt=True'
  'TrustServerCertificate=False'
  'Connection Timeout=60'
  'Authentication=Active Directory Managed Identity'
  'User Id=${managedIdentityClientId}'
], ';')

// ---------------------------------------------------------------------------------------------
// THE KEY VAULT REFERENCE.
//
//   @Microsoft.KeyVault(VaultName=<vault>;SecretName=<name>)
//
// App Service resolves this at startup: it takes a token for the vault using the identity named
// by `keyVaultReferenceIdentity`, reads the secret, and injects the VALUE into the process
// environment. The application reads an ordinary environment variable and never knows a vault
// was involved.
//
// What that buys, precisely:
//   - the secret is not in the template, the parameter file, git, or the deployment history
//   - the setting stored on the site is the reference string, so anyone who dumps app settings
//     sees the pointer and not the value
//   - rotating it is a vault operation, and the app picks it up on its next restart
//
// NO VERSION is pinned. `SecretName=x` without `;SecretVersion=y` follows the current version,
// which is what makes rotation a vault-only operation. Pinning a version is the safer-sounding
// choice and it means every rotation requires a redeployment — which is how secrets end up
// un-rotated.
//
// The honest limit: once resolved, the value IS in the process environment, so anything that can
// read the app's memory or run code inside it can read the secret. Key Vault references remove
// the secret from configuration and from source control. They do not make a compromised process
// safe, and no configuration setting can.
// ---------------------------------------------------------------------------------------------
var thirdPartySecretReference = '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=${thirdPartySecretName})'

resource site 'Microsoft.Web/sites@2023-12-01' = {
  name: siteName
  location: location
  tags: tags

  // -------------------------------------------------------------------------------------------
  // The identity is ATTACHED here and CREATED elsewhere. That separation is what let every role
  // assignment happen before this resource existed — see the dependency notes in main.bicep.
  // -------------------------------------------------------------------------------------------
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${managedIdentityId}': {}
    }
  }

  properties: {
    serverFarmId: plan.id
    httpsOnly: true

    // Which identity resolves Key Vault references. Required, not optional, for a user-assigned
    // setup: the default is the SYSTEM-assigned identity, and this site does not have one. Leave
    // it out and every reference fails with an access error naming a principal that does not
    // exist.
    keyVaultReferenceIdentity: managedIdentityId

    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true

      appSettings: [
        // ---- endpoints and identifiers. Public by design. --------------------------------
        {
          name: 'AZURE_CLIENT_ID'
          value: managedIdentityClientId
        }
        {
          name: 'ServiceBus__FullyQualifiedNamespace'
          value: serviceBusFqdn
        }
        {
          name: 'ServiceBus__QueueName'
          value: serviceBusQueueName
        }
        {
          name: 'KeyVault__Name'
          value: keyVaultName
        }
        {
          name: 'Entra__ClientId'
          value: entraClientId
        }
        {
          name: 'Entra__TenantId'
          value: tenantId
        }

        // ---- a connection string that carries no credential -------------------------------
        {
          name: 'ConnectionStrings__Sql'
          value: sqlConnectionString
        }

        // ---- THE KEY VAULT REFERENCE. A pointer, not a value. -----------------------------
        {
          name: 'ThirdParty__WebhookSigningKey'
          value: thirdPartySecretReference
        }

        // ---- platform --------------------------------------------------------------------
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
      ]

      // The slot purpose-built for connection strings, left EMPTY rather than omitted. An empty
      // array is a statement; an absent property is an oversight that looks identical.
      connectionStrings: []
    }
  }
}

// ---------------------------------------------------------------------------------------------
// EASY AUTH — Entra ID, validation only, and therefore no client secret.
//
// This is the choice that keeps the caller-to-API path secret-free, and it turns on the
// difference between two things App Service Authentication can do:
//
//   ISSUING tokens    the app redirects an unauthenticated browser to Entra, receives an
//                     authorization code, and exchanges it for a token. That exchange requires
//                     the app to prove it is itself — with a CLIENT SECRET, which would then
//                     have to live in app settings or a vault. This configuration does not do
//                     this.
//
//   VALIDATING tokens the caller already holds a token and presents it. The app checks the
//                     signature against Entra's published keys, checks the issuer, and checks
//                     the audience. All three are public information. NO SECRET IS NEEDED.
//
// `unauthenticatedClientAction: 'Return401'` is what selects the second: no redirect is ever
// issued, so no code exchange ever happens, so no client secret is ever required. For an API
// this is also simply the correct behaviour — a JSON client handles 401 and cannot follow a
// login redirect meaningfully.
//
// Rejection happens in the PLATFORM, before a request reaches application code. An unauthorised
// request never runs a line of the app, which is a stronger position than middleware inside a
// process that has already been entered.
// ---------------------------------------------------------------------------------------------
resource authSettings 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: site
  name: 'authsettingsV2'
  properties: {
    platform: {
      enabled: true
      runtimeVersion: '~1'
    }

    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'Return401'

      // /health stays open on purpose. A probe that has to authenticate cannot tell "the app is
      // down" from "the token is wrong", and the platform's own health checks carry no token.
      excludedPaths: [
        '/health'
      ]
    }

    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          // Built from `environment()` rather than hard-coded. The linter flags a literal
          // login.microsoftonline.com, and it is right to: the same template deployed to a
          // sovereign cloud would point its token validation at an endpoint that does not
          // exist there, and the failure would look like a broken app registration.
          openIdIssuer: '${environment().authentication.loginEndpoint}${tenantId}/v2.0'
          clientId: entraClientId

          // `clientSecretSettingName` is ABSENT. See the block above — this is the line whose
          // absence is the deliverable.
        }
        validation: {
          // Which audiences this API accepts. A token minted for a DIFFERENT application is
          // signed by the same tenant and is still not for this API; without this check it
          // would be accepted, which is the classic confused-deputy hole.
          allowedAudiences: [
            'api://${entraClientId}'
            entraClientId
          ]
        }
      }
    }

    login: {
      // No token store. It exists to cache tokens for a login flow this app does not perform,
      // and it writes them to the site's file system — state to protect, for no benefit here.
      tokenStore: {
        enabled: false
      }
    }
  }
}

output siteName string = site.name
output defaultHostName string = site.properties.defaultHostName
output principalId string = managedIdentityClientId
