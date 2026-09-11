// =============================================================================================
// Day 27 — the data tier behind private endpoints.
//
// Days 23-25 secured the data tier with IDENTITY: Entra-only SQL, local auth disabled on Service
// Bus, RBAC on Key Vault. That is strong, and it is one layer. Every one of those resources was
// still reachable from anywhere on the internet, so a stolen or misissued token was usable from
// anywhere on the internet too.
//
// This template removes the network path. After it:
//
//   publicNetworkAccess: 'Disabled'   the PaaS endpoint refuses connections from outside
//   private endpoint + DNS            the name resolves to a private IP inside the VNet
//
// The two together mean an attacker needs a foothold INSIDE the VNet before a stolen credential
// is worth anything. Identity says who you are; the network says where you may say it from.
//
// ---------------------------------------------------------------------------------------------
// THE TRADE, STATED PLAINLY
//
// This breaks laptop access, permanently and by design. After deployment no developer machine can
// reach SQL, Service Bus or the vault — not with the right token, not from the right account.
// Day 25's grant script and Day 26's local worker both stop working, and the replacements are a
// jump box, a VPN, or running the tools inside the VNet.
//
// That cost is the reason private endpoints get deferred, and deferring them is why "we use
// managed identity" so often sits in front of a database the whole internet can open a socket to.
// =============================================================================================

targetScope = 'resourceGroup'

@allowed([ 'dev' ])
param environmentName string = 'dev'

@allowed([
  'centralindia'
  'indonesiacentral'
  'malaysiawest'
  'uaenorth'
  'koreacentral'
])
param location string = 'centralindia'

@description('Object id of the Entra principal that administers SQL. A group in production.')
param sqlAdminObjectId string

@description('Display name for that principal.')
param sqlAdminLogin string

var nameSuffix = take(uniqueString(resourceGroup().id), 6)

var vnetName = 'vnet-quotes-${environmentName}-${nameSuffix}'
var sqlServerName = 'sql-quotes-${environmentName}-${nameSuffix}'
var serviceBusName = 'sb-quotes-${environmentName}-${nameSuffix}'
var keyVaultName = 'kv-quotes-${environmentName}-${nameSuffix}'
var databaseName = 'quotes'

var tags = {
  application: 'quotes-api'
  environment: environmentName
  managedBy: 'bicep'
  source: 'Day27/infra/main.bicep'
}

// ---------------------------------------------------------------------------------------------
// The network.
//
// Two subnets, because they have incompatible requirements and sharing one is a mistake that
// only shows up later:
//
//   snet-data   holds the private endpoint NICs. Needs privateEndpointNetworkPolicies disabled.
//   snet-app    where compute would run. Needs a delegation, which endpoints must not have.
//
// /24 each out of a /16. Generous, and free — address space inside a VNet costs nothing, and
// resizing a subnet that has resources in it is materially harder than over-allocating now.
// ---------------------------------------------------------------------------------------------
resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: vnetName
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [ '10.20.0.0/16' ]
    }
    subnets: [
      {
        name: 'snet-data'
        properties: {
          addressPrefix: '10.20.1.0/24'

          // Required for private endpoints. Network policies (NSG and UDR evaluation) are applied
          // to the endpoint NIC when enabled, and an NSG rule that blocks the endpoint's own
          // traffic produces a connection failure with no obvious cause. Disabling is the
          // documented prerequisite, not a relaxation of security: access is still governed by
          // the private link connection itself.
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
      {
        name: 'snet-app'
        properties: {
          addressPrefix: '10.20.2.0/24'

          // Where a Container App environment or App Service VNet integration would attach.
          // Nothing is delegated yet, because nothing runs here yet — see the honest gap in
          // EXERCISE.md about the application still being outside the VNet.
        }
      }
    ]
  }
}

// ---------------------------------------------------------------------------------------------
// The data tier. Same identity posture as Days 23-25, plus the network change.
// ---------------------------------------------------------------------------------------------

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'

    // The line this template exists for. Day 23-26 left this 'Enabled' with a firewall rule.
    publicNetworkAccess: 'Disabled'

    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'User'
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

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
    autoPauseDelay: 60
    minCapacity: json('0.5')
    zoneRedundant: false
  }
}

// NOTE: there is deliberately NO firewall rule here. With publicNetworkAccess disabled the
// firewall is not consulted at all, and leaving an "allow Azure services" rule behind would be
// misleading dead configuration that a future reader might take for a live allowance.

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: serviceBusName
  location: location
  tags: tags
  sku: {
    // Premium, and NOT a free choice. Private endpoints are unavailable on Standard and Basic —
    // Microsoft gates VNet integration to the dedicated tier. This is the largest cost in the
    // template and the reason the honest gap in EXERCISE.md exists.
    name: 'Premium'
    tier: 'Premium'
    capacity: 1
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
  }
}

resource topic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBus
  name: 'quote-events'
  properties: {
    defaultMessageTimeToLive: 'P1D'
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7

    publicNetworkAccess: 'Disabled'
    networkAcls: {
      // Deny by default once public access is off. `bypass: 'AzureServices'` is deliberately NOT
      // set: it is a broad exemption for a large set of first-party services, and nothing here
      // needs it. An exemption that is not needed is an exemption that cannot be audited.
      defaultAction: 'Deny'
      bypass: 'None'
    }
  }
}

// ---------------------------------------------------------------------------------------------
// The endpoints. One per resource, each with its own DNS zone.
//
// The zone names are service-specific and exact. `privatelink.database.windows.net` is not a
// convention this template chose — it is the name Azure's public DNS CNAMEs to, which is what
// makes the override work without any client changing a connection string.
// ---------------------------------------------------------------------------------------------

module sqlEndpoint 'modules/private-endpoint.bicep' = {
  name: 'pe-sql'
  params: {
    location: location
    name: 'sql'
    targetResourceId: sqlServer.id
    groupId: 'sqlServer'
    privateDnsZoneName: 'privatelink${environment().suffixes.sqlServerHostname}'
    subnetId: vnet.properties.subnets[0].id
    virtualNetworkId: vnet.id
    tags: tags
  }
}

module serviceBusEndpoint 'modules/private-endpoint.bicep' = {
  name: 'pe-servicebus'
  params: {
    location: location
    name: 'servicebus'
    targetResourceId: serviceBus.id
    groupId: 'namespace'
    privateDnsZoneName: 'privatelink.servicebus.windows.net'
    subnetId: vnet.properties.subnets[0].id
    virtualNetworkId: vnet.id
    tags: tags
  }
}

module keyVaultEndpoint 'modules/private-endpoint.bicep' = {
  name: 'pe-keyvault'
  params: {
    location: location
    name: 'keyvault'
    targetResourceId: keyVault.id
    groupId: 'vault'
    privateDnsZoneName: 'privatelink.vaultcore.azure.net'
    subnetId: vnet.properties.subnets[0].id
    virtualNetworkId: vnet.id
    tags: tags
  }
}

output vnetName string = vnet.name
output sqlServerName string = sqlServer.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output serviceBusName string = serviceBus.name
output keyVaultName string = keyVault.name

output sqlPrivateIp string = sqlEndpoint.outputs.privateIpAddress
output serviceBusPrivateIp string = serviceBusEndpoint.outputs.privateIpAddress
output keyVaultPrivateIp string = keyVaultEndpoint.outputs.privateIpAddress
