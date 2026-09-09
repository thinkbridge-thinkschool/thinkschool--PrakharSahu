// =============================================================================================
// Day 25 — Identity end to end.
//
// The claim this template exists to make: there is no secret anywhere in the path from the app
// to its data. Not in app settings, not in a connection string slot, not in a parameter, not in
// the deployment history, and not in this file.
//
// ---------------------------------------------------------------------------------------------
// THE THREE PATHS, AND HOW EACH ONE STOPPED NEEDING A SECRET
//
//   API -> SQL           managed identity + Entra-only server. There is no password to store
//                        because the server refuses passwords: azureADOnlyAuthentication is on,
//                        so `administratorLoginPassword` is not merely absent, it is unusable.
//
//   API -> Service Bus   managed identity + `disableLocalAuth: true`. SAS keys still exist as an
//                        Azure concept, but the namespace rejects them, so a leaked one is worth
//                        nothing.
//
//   caller -> API        Entra ID, token validation only. Easy Auth verifies a JWT and never
//                        performs a login redirect, which is what removes the client secret an
//                        interactive auth-code flow would otherwise require.
//
// What is left over is one third-party credential that cannot be replaced by an identity,
// because the third party does not speak Entra. That is what Key Vault is for — and the app
// setting holds a REFERENCE to it, never the value.
//
// ---------------------------------------------------------------------------------------------
// WHY A USER-ASSIGNED IDENTITY RATHER THAN SYSTEM-ASSIGNED
//
// Days 23 and 24 used system-assigned, which is simpler and was right there. It does not work
// here, and the reason is ordering rather than preference.
//
// A system-assigned identity does not exist until its host resource is created, so every role
// assignment that grants it access must come AFTER the app. Key Vault references resolve when
// the app starts — which is during that same deployment, before the grant exists. The app boots,
// tries to read the vault, is refused, and caches a failed reference.
//
// A user-assigned identity is an independent resource. It can be created first and granted
// everything first, so by the time the app exists its identity already has every permission it
// will ever need. The dependency graph at the bottom of this file is that argument in code.
// =============================================================================================

targetScope = 'resourceGroup'

@description('Short environment name. Drives resource names and nothing else.')
@allowed([ 'dev' ])
param environmentName string = 'dev'

@description('''
Deployment region. Constrained because this subscription carries a `sys.regionrestriction`
policy permitting only these, discovered the hard way on Day 17.
''')
@allowed([
  'centralindia'
  'indonesiacentral'
  'malaysiawest'
  'uaenorth'
  'koreacentral'
])
param location string = 'centralindia'

@description('Object id of the Entra principal that administers SQL. A group in production; the deploying user here.')
param sqlAdminObjectId string

@description('Display name for that principal.')
param sqlAdminLogin string

@description('''
Application (client) id of the Entra app registration that fronts this API.

Created by scripts/entra-app.sh, not by this template, because an app registration is a Microsoft
Graph object and ARM cannot create one. This is a public identifier — a client id is designed to
be embedded in browser code — so it is a plain parameter and not a secure one.
''')
param entraClientId string

@description('Name of the secret held in Key Vault. The VALUE is never passed to this template.')
param thirdPartySecretName string = 'payments-webhook-signing-key'

// ---------------------------------------------------------------------------------------------
// Naming. Centralised so every derived value — an FQDN, a vault URI, a connection string — is
// known before the first resource is submitted. Day 23 learned this the expensive way: a name
// built inside a module is unknown until that module runs, and everything downstream of it stops
// being analysable.
// ---------------------------------------------------------------------------------------------
var nameSuffix = take(uniqueString(resourceGroup().id), 6)

var identityName = 'id-identity-${environmentName}-${nameSuffix}'
var keyVaultName = 'kv-idty-${environmentName}-${nameSuffix}'
var sqlServerName = 'sql-identity-${environmentName}-${nameSuffix}'
var serviceBusName = 'sb-identity-${environmentName}-${nameSuffix}'
var planName = 'plan-identity-${environmentName}-${nameSuffix}'
var siteName = 'app-identity-${environmentName}-${nameSuffix}'
var workspaceName = 'log-identity-${environmentName}-${nameSuffix}'

var databaseName = 'identity'
var queueName = 'identity-probe'

var sqlServerFqdn = '${sqlServerName}${environment().suffixes.sqlServerHostname}'
var serviceBusFqdn = '${serviceBusName}.servicebus.windows.net'

var tags = {
  application: 'identity-e2e'
  environment: environmentName
  managedBy: 'bicep'
  source: 'Day25/infra/main.bicep'
}

// ---------------------------------------------------------------------------------------------
// 1. The identity. First, because everything else grants access TO it.
// ---------------------------------------------------------------------------------------------
module identity 'modules/identity.bicep' = {
  name: 'identity'
  params: {
    location: location
    identityName: identityName
    tags: tags
  }
}

// ---------------------------------------------------------------------------------------------
// 2. The data plane.
// ---------------------------------------------------------------------------------------------
module observability 'modules/observability.bicep' = {
  name: 'observability'
  params: {
    location: location
    workspaceName: workspaceName
    tags: tags
  }
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    location: location
    keyVaultName: keyVaultName
    tags: tags
  }
}

module sql 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    location: location
    serverName: sqlServerName
    databaseName: databaseName
    sqlAdminObjectId: sqlAdminObjectId
    sqlAdminLogin: sqlAdminLogin
    tags: tags
  }
}

module serviceBus 'modules/servicebus.bicep' = {
  name: 'servicebus'
  params: {
    location: location
    namespaceName: serviceBusName
    queueName: queueName
    tags: tags
  }
}

// ---------------------------------------------------------------------------------------------
// 3. RBAC — every grant the identity needs, all of it BEFORE the app exists.
//
// This is the module that replaces the connection strings this template does not have. Each
// assignment is the narrowest built-in role that covers the operation the app actually performs:
//
//   Key Vault Secrets User          read a secret value. Not `Key Vault Administrator`, which
//                                   also grants write and delete on every secret in the vault.
//   Azure Service Bus Data Sender   send. Not `Data Owner`, which also grants manage.
//   Azure Service Bus Data Receiver receive, kept separate from send so the pair is auditable.
//
// SQL is deliberately absent from this list. Reaching the SERVER is an Entra token; being
// allowed to read a TABLE is a database-level grant that ARM cannot express — see the output at
// the bottom of this file.
// ---------------------------------------------------------------------------------------------
module rbac 'modules/rbac.bicep' = {
  name: 'rbac'
  params: {
    keyVaultName: keyVaultName
    serviceBusNamespaceName: serviceBusName
    principalId: identity.outputs.principalId
  }
  dependsOn: [
    keyVault
    serviceBus
  ]
}

// ---------------------------------------------------------------------------------------------
// 4. The app. Last, so that when it starts its identity already has every permission.
//
// `dependsOn: [rbac]` is the whole ordering argument made explicit. Without it ARM is free to
// create the app in parallel with the role assignments, the app resolves its Key Vault reference
// before the grant lands, and the reference fails. Day 24 was bitten by exactly this shape of
// bug — an `existing` lookup that created no dependency — so here it is stated rather than
// hoped for.
// ---------------------------------------------------------------------------------------------
module app 'modules/app.bicep' = {
  name: 'app'
  params: {
    location: location
    planName: planName
    siteName: siteName
    tags: tags

    managedIdentityId: identity.outputs.resourceId
    managedIdentityClientId: identity.outputs.clientId

    entraClientId: entraClientId
    tenantId: subscription().tenantId

    sqlServerFqdn: sqlServerFqdn
    sqlDatabaseName: databaseName
    serviceBusFqdn: serviceBusFqdn
    serviceBusQueueName: queueName

    keyVaultName: keyVaultName
    thirdPartySecretName: thirdPartySecretName

    appInsightsConnectionString: observability.outputs.connectionString
  }
  dependsOn: [
    rbac
  ]
}

// ---------------------------------------------------------------------------------------------
// Outputs. Every one of these is a public identifier or an endpoint. None is a credential.
// ---------------------------------------------------------------------------------------------

output siteName string = siteName
output siteUrl string = 'https://${app.outputs.defaultHostName}'
output identityClientId string = identity.outputs.clientId
output identityPrincipalId string = identity.outputs.principalId
output identityName string = identityName

output keyVaultName string = keyVaultName
output keyVaultUri string = keyVault.outputs.vaultUri
output thirdPartySecretName string = thirdPartySecretName

output sqlServerFqdn string = sqlServerFqdn
output sqlDatabaseName string = databaseName
output serviceBusFqdn string = serviceBusFqdn
output serviceBusQueueName string = queueName

@description('''
The step ARM cannot perform.

A managed identity holding an Entra token can REACH the SQL server. Being allowed to read a table
is a database-level grant — `CREATE USER ... FROM EXTERNAL PROVIDER` — executed inside the
database in T-SQL. There is no ARM resource for it, so a template alone produces an app that
authenticates successfully and cannot read a row.

Emitted here rather than living in somebody's notes, and applied by tools/SqlGrant.
''')
output grantDatabaseAccessScript string = join([
  'CREATE USER [${identityName}] FROM EXTERNAL PROVIDER;'
  'ALTER ROLE db_datareader ADD MEMBER [${identityName}];'
  'ALTER ROLE db_datawriter ADD MEMBER [${identityName}];'
  'ALTER ROLE db_ddladmin ADD MEMBER [${identityName}];'
], ' ')
