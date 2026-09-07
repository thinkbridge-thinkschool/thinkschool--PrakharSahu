// =============================================================================================
// Azure SQL for the Dispatch capstone.
//
// Day 22 piece 2 shipped with every store as a ConcurrentDictionary and said so out loud:
// "Picking a database on day one means picking it before the aggregate boundaries have met a
// single real requirement." Those boundaries have now been through a design review, so this is
// where the choice gets made.
//
// THE DEFINING DECISION IN THIS FILE: there is no SQL administrator password. Anywhere.
// Authentication is Entra-only, and the API reaches the database with its managed identity —
// the same mechanism Day 17 used to reach the Quotes API. A connection string with a password
// in it is a credential that has to be stored, rotated and eventually leaked; this has none.
// =============================================================================================

@description('Deployment region. Constrained to the five this subscription\'s policy permits.')
@allowed([
  'centralindia'
  'indonesiacentral'
  'malaysiawest'
  'uaenorth'
  'koreacentral'
])
param location string

@description('''
Server name, computed by the caller.

The module does NOT build its own name, and that is deliberate. A name assembled inside a module
is invisible to `what-if` until the module runs, which short-circuits validation of everything
downstream that depends on it. Naming in the composition root keeps every derived value —
resource ids, FQDNs, connection strings — computable before a single resource is submitted.
''')
param serverName string

@description('Database SKU. Serverless in dev so an idle database costs nothing; provisioned in prod.')
param databaseSku object

@description('Object id of the Entra group or user that administers the server. NOT a password.')
param sqlAdminObjectId string

@description('Display name for that administrator, shown in the portal.')
param sqlAdminLogin string

@description('Backup retention in days. Short in dev, long in prod.')
@minValue(1)
@maxValue(35)
param backupRetentionDays int

@description('Zone redundancy. Costs more and is wasted on a dev database.')
param zoneRedundant bool

@description('Tags applied to every resource in this module.')
param tags object

// ---------------------------------------------------------------------------------------------
// The logical server.
//
// `publicNetworkAccess: Enabled` plus the Azure-services firewall rule below is the smallest
// thing that lets a Container App reach it. A private endpoint is the right answer for a real
// production system and is deliberately out of scope here — noted rather than silently skipped.
// ---------------------------------------------------------------------------------------------
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: serverName
  location: location
  tags: tags
  properties: {
    // Entra-only. There is no `administratorLogin` / `administratorLoginPassword` pair, which
    // is the point: those two properties are the reason most Bicep templates need a Key Vault
    // reference and a secure parameter. Removing the password removes the whole problem.
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    restrictOutboundNetworkAccess: 'Disabled'
  }
}

// ---------------------------------------------------------------------------------------------
// Let Azure services reach it.
//
// 0.0.0.0 is the documented sentinel for "any Azure service", not a literal address and not
// "the whole internet" — the server still refuses anything that cannot present an Entra token.
// A Container App has no stable outbound IP on the Consumption plan, so an IP allow-list is not
// available without moving to a VNet-integrated workload profile.
// ---------------------------------------------------------------------------------------------
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// ---------------------------------------------------------------------------------------------
// The database.
//
// Every dimension that differs between dev and prod arrives as a parameter rather than being
// branched on `environmentName` inside this file. A module that says `if (environmentName ==
// 'prod')` has two behaviours and one name; a module that takes a SKU object has one behaviour
// and is honest about what varies.
// ---------------------------------------------------------------------------------------------
resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'dispatch'
  location: location
  tags: tags
  sku: databaseSku
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    zoneRedundant: zoneRedundant

    // Serverless only. Null in dev's parameter file would be wrong — the property is simply
    // ignored by provisioned tiers, so passing it unconditionally keeps the module uniform.
    autoPauseDelay: databaseSku.tier == 'GeneralPurpose' && contains(databaseSku.name, 'S_Gen5') ? 60 : -1

    // Backups are the one thing worth over-provisioning in dev too: the cost is trivial and the
    // day you need one is never the day you planned for it.
    requestedBackupStorageRedundancy: zoneRedundant ? 'Zone' : 'Local'
  }
}

resource shortTermRetention 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01-preview' = {
  parent: database
  name: 'default'
  properties: {
    retentionDays: backupRetentionDays
  }
}

// ---------------------------------------------------------------------------------------------
// Outputs.
//
// The connection string carries NO credential — `Authentication=Active Directory Default` tells
// the driver to go and get a token from the platform, exactly as DefaultAzureCredential does for
// everything else in this repository. It is safe to emit as a plain output for the same reason
// the Day 17 broker's environment variables were safe to print in full: there is nothing in it
// worth stealing.
// ---------------------------------------------------------------------------------------------
output serverName string = sqlServer.name
output serverFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = database.name

output connectionString string = join([
  'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433'
  'Initial Catalog=${database.name}'
  'Encrypt=True'
  'TrustServerCertificate=False'
  'Connection Timeout=30'
  'Authentication=Active Directory Default'
], ';')
