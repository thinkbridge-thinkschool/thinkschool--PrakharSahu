// =============================================================================================
// Azure SQL — Entra-only. There is no administrator password, and none can be set.
//
// ---------------------------------------------------------------------------------------------
// THE LINE THAT MATTERS IS `azureADOnlyAuthentication: true`
//
// Without it, a server can have BOTH an Entra admin and a SQL login, and the SQL login is the
// one that ends up in a connection string. "We use managed identity" is then true of the code
// somebody wrote last sprint and false of the batch job written two years ago, and the password
// is still valid, still un-rotated, and still in whatever it was pasted into.
//
// With it, password authentication is refused at the server. `administratorLoginPassword` is not
// merely omitted from this template — it is unusable, so there is nothing to leak, nothing to
// rotate, and no second path to close later.
//
// The trade is real and worth stating: every client must now be able to obtain an Entra token.
// A legacy tool that only speaks SQL logins cannot connect at all. That is the point, and it is
// also why this is a decision to make deliberately rather than a checkbox to tick.
// =============================================================================================

param location string
param serverName string
param databaseName string
param sqlAdminObjectId string
param sqlAdminLogin string
param tags object

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: serverName
  location: location
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    restrictOutboundNetworkAccess: 'Disabled'

    // The Entra administrator. A principal that can hold members, so that administration
    // survives a person leaving; here it is the deploying user, which is a compromise the
    // deploy script names out loud.
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'User'
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: subscription().tenantId

      // The whole point of this module.
      azureADOnlyAuthentication: true
    }
  }
}

// ---------------------------------------------------------------------------------------------
// Serverless, and small. This database exists to prove an authentication path, not to hold data.
//
// GP_S_Gen5_1 auto-pauses after an hour idle, so an environment nobody is using bills for
// storage and nothing else. The cost is a cold start of several seconds on the first query after
// a pause — which shows up in the identity probe as a slow response, not a failure.
// ---------------------------------------------------------------------------------------------
resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  tags: tags
  sku: {
    name: 'GP_S_Gen5_1'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 1
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    autoPauseDelay: 60
    minCapacity: json('0.5')
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Local'
  }
}

// ---------------------------------------------------------------------------------------------
// Let Azure services reach it.
//
// 0.0.0.0 to 0.0.0.0 is the documented sentinel for "any Azure service" — not a literal address
// and not "the whole internet". The server still refuses anything that cannot present an Entra
// token, which is what makes an open-looking firewall rule defensible here and would not make it
// defensible on a server that accepted passwords.
//
// The name avoids the substring "Windows": azd's pre-deploy linter flags it as a reserved word
// and claims the deployment will fail. It does not, but a warning that cries wolf on every run
// gets skimmed, and Day 24 proved that is exactly when a real one gets missed.
// ---------------------------------------------------------------------------------------------
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output serverName string = sqlServer.name
output databaseName string = database.name
output fullyQualifiedDomainName string = sqlServer.properties.fullyQualifiedDomainName
