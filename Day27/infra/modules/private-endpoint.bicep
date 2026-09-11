// =============================================================================================
// One private endpoint, and the DNS that makes it work.
//
// A private endpoint on its own does almost nothing useful. It places a NIC with a private IP in
// your subnet and wires it to the PaaS resource — but clients connect by NAME, and the public DNS
// for `sql-x.database.windows.net` still resolves to a public IP. Without the DNS half, every
// client dutifully resolves the public address, finds `publicNetworkAccess: Disabled`, and fails.
//
// So this module always deploys three things together:
//
//   1. the endpoint          a NIC in the subnet, linked to the resource by groupId
//   2. a private DNS zone    privatelink.<service>.<suffix>, holding the private A record
//   3. a zone group          the link that makes Azure write that A record automatically
//
// Keeping them in one module means they cannot be deployed apart, which is the single most
// common way a private-endpoint rollout half-works: the endpoint exists, the name still resolves
// publicly, and the failure looks like a firewall problem.
// =============================================================================================

@description('Deployment region. Must match the VNet.')
param location string

@description('Short name for the endpoint, e.g. "sql".')
param name string

@description('Resource id of the PaaS resource to put behind the endpoint.')
param targetResourceId string

@description('''
Sub-resource to connect to, e.g. `sqlServer`, `namespace`, `vault`.

Service-specific and not guessable — a resource with several sub-resources (Storage has blob,
file, queue, table) needs one endpoint per sub-resource, and using the wrong groupId produces a
deployment that succeeds and a connection that does not.
''')
param groupId string

@description('Private DNS zone name, e.g. privatelink.database.windows.net.')
param privateDnsZoneName string

@description('Resource id of the subnet holding the endpoint NIC.')
param subnetId string

@description('Resource id of the VNet the zone is linked to.')
param virtualNetworkId string

param tags object

resource privateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  // Private DNS zones are GLOBAL resources. `location: 'global'` is not a placeholder — passing a
  // region here is a deployment error, and it catches people out because every neighbouring
  // resource in the template is regional.
  name: privateDnsZoneName
  location: 'global'
  tags: tags
}

// Without this link the zone exists and nothing consults it. The zone is a container of records;
// the LINK is what makes a VNet's resolver look in it.
resource vnetLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: privateDnsZone
  name: '${name}-vnet-link'
  location: 'global'
  properties: {
    virtualNetwork: {
      id: virtualNetworkId
    }
    // No auto-registration. That feature registers VM hostnames, and this zone holds exactly one
    // record written by the zone group below. Leaving it on would let a VM claim a name in a zone
    // that is load-bearing for data-tier resolution.
    registrationEnabled: false
  }
}

resource privateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: 'pe-${name}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: subnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${name}-connection'
        properties: {
          privateLinkServiceId: targetResourceId
          groupIds: [
            groupId
          ]
        }
      }
    ]
  }
}

// The zone group is what writes the A record. Deploying the endpoint and the zone without this is
// the half-working state described in the header: private IP allocated, name still public.
resource zoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
  parent: privateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: replace(privateDnsZoneName, '.', '-')
        properties: {
          privateDnsZoneId: privateDnsZone.id
        }
      }
    ]
  }
  dependsOn: [
    vnetLink
  ]
}

output privateEndpointId string = privateEndpoint.id
output privateDnsZoneId string = privateDnsZone.id

@description('The private IP the endpoint received. Proof the NIC landed in the subnet.')
output privateIpAddress string = privateEndpoint.properties.customDnsConfigs[0].ipAddresses[0]
