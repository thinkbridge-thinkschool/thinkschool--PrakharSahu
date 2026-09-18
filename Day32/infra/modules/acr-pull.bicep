// AcrPull for the API's identity, on a registry that belongs to another resource group.
//
// The role is granted at the registry rather than at the subscription or the group: the app
// needs to read one registry's images and nothing else, and a scope any wider would be a
// permission nobody would notice was too broad until it mattered.

param containerRegistryName string

@description('Object id of the identity being granted the role. NOT the client id — a role assignment names the Entra object.')
param principalId string

resource registry 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' existing = {
  name: containerRegistryName
}

// 7f951dda-4ed3-4680-a7ca-43fe172d538d — AcrPull. Hard-coded because role definition GUIDs are
// stable across every Azure tenant, and looking it up by name at deploy time needs a reader role
// on the definition list that a least-privilege deployer should not have.
var acrPullRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d'
)

resource assignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry

  // The name must be a GUID, deterministic, and unique per (scope, principal, role). guid() over
  // exactly those three is the documented recipe: it makes a redeploy idempotent instead of
  // failing with RoleAssignmentExists, and it makes two different principals on the same registry
  // distinct instead of overwriting each other.
  name: guid(registry.id, principalId, acrPullRoleDefinitionId)

  properties: {
    roleDefinitionId: acrPullRoleDefinitionId
    principalId: principalId

    // Without this, a deployment that runs before Entra has replicated the new identity fails
    // with "principal does not exist". Telling ARM the type up front skips the lookup entirely.
    principalType: 'ServicePrincipal'
  }
}
