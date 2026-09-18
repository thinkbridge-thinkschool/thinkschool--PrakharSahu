// =================================================================================================
// One Azure SQL server, two databases.
//
// THE DEFINING DECISION: there is no administrator password. Anywhere. Not in a parameter, not in
// Key Vault, not in a pipeline variable. Authentication is Entra-only and both applications reach
// their database with a managed identity.
//
// That is not security theatre — it deletes a category of work. No rotation schedule, no secret to
// provision, no @secure() parameter to keep out of a deployment log, no output to redact. The two
// properties that would have carried it, administratorLogin and administratorLoginPassword, are
// simply absent from this file.
//
// WHY ONE SERVER AND TWO DATABASES rather than two servers, or one database with two schemas:
//
//   * Two servers would double the Entra administrator configuration and the firewall rules for no
//     isolation gain — a logical server is a management boundary, not a security one.
//   * One database would let a bug in Quotes take Dispatch down with it, and would put two
//     applications' migration histories in one __EFMigrationsHistory table, where a rollback of one
//     silently concerns the other.
//
// Two databases on one server keeps them independently restorable and independently pausable while
// costing exactly one server's worth of configuration, which is zero.
// =================================================================================================

param location string
param serverName string

@description('Databases to create. Each gets its own serverless compute and its own auto-pause clock.')
param databaseNames array

@description('Entra OBJECT id of the administrator. "Object id" is said precisely — see identity.bicep.')
param sqlAdminObjectId string

param sqlAdminLogin string

@allowed([ 'User', 'Group' ])
param sqlAdminPrincipalType string

param tags object

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: serverName
  location: location
  tags: tags
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: sqlAdminPrincipalType
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: subscription().tenantId

      // The line that closes the door. With this false, SQL logins keep working alongside Entra and
      // the server is one CREATE LOGIN away from having a password again.
      azureADOnlyAuthentication: true
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    restrictOutboundNetworkAccess: 'Disabled'
  }
}

// 0.0.0.0-0.0.0.0 is the documented sentinel for "any Azure service". It is not a literal address
// and it is not "the whole internet": the server still refuses anything that cannot present an
// Entra token for a principal it has a user for.
//
// It is here because a Container App on the Consumption plan has no stable outbound IP, so an
// allow-list is not available without moving to a VNet-integrated workload profile. Day 27 built
// exactly that — private endpoints, public access disabled, DNS zone groups — and it cost about
// Rs 2,000 a day, which is the honest reason this ship does not have one.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// -------------------------------------------------------------------------------------------------
// Serverless, with auto-pause.
//
// GP_S_Gen5_1, minimum 0.5 vCores, paused after 60 idle minutes. Paused means the compute bill stops
// entirely and only storage is charged — a couple of rupees a day for databases this size. It is the
// single reason four applications and two databases are affordable to leave running on a student
// subscription that is already hosting four other deployments.
//
// The price is a cold start: the first connection after a pause takes 30 to 60 seconds and arrives
// as a timeout rather than as "please wait". Both applications are built for it — every persistence
// registration turns on EnableRetryOnFailure — but a human opening the URL cold still sees a long
// blank page. That tradeoff is taken deliberately and written up in EXERCISE.md rather than left
// for whoever demos this next to discover.
// -------------------------------------------------------------------------------------------------
resource databases 'Microsoft.Sql/servers/databases@2023-08-01-preview' = [for dbName in databaseNames: {
  parent: sqlServer
  name: dbName
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
    maxSizeBytes: 2147483648
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Local'
  }
}]

// Seven days, the minimum that still covers "somebody broke it on Friday". Backup storage is charged
// beyond the database size, so this is not free, but at 2 GB it is close to it.
resource retention 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01-preview' = [for (dbName, i) in databaseNames: {
  name: '${serverName}/${dbName}/default'
  properties: {
    retentionDays: 7
  }
  dependsOn: [ databases[i] ]
}]

output serverName string = sqlServer.name
output serverFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseNames array = databaseNames
