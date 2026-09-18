// =================================================================================================
// Send and Receive on Day 26's Service Bus namespace, granted to this deployment's identity.
//
// WHY THIS FILE EXISTS, WHICH IS A STORY ABOUT GETTING IT WRONG FIRST
//
// The first version of this deployment read the namespace's SAS connection string, put it in Key
// Vault, and handed it to the API. Every consumer then failed on a loop with:
//
//   UnauthorizedAccessException: Put token failed. status-code: 401, status-description:
//   LocalAuthDisabled: Authorization failed because SAS authentication has been disabled
//   for the namespace.
//
// The namespace has `disableLocalAuth: true`, so its own SAS keys are rejected outright — which
// Day 26 chose deliberately, and which MessagingExtensions.cs even documents in a comment. The
// credential was fetched, stored and mounted perfectly, and was worth nothing.
//
// The fix is not to re-enable SAS. It is to stop carrying a credential at all: the application
// already supports `ServiceBus:FullyQualifiedNamespace` with a TokenCredential, so it gets a token
// from the platform exactly as it does for SQL. That deletes the second Key Vault secret and
// leaves this deployment with precisely one.
//
// TWO ROLES, NOT ONE. `Azure Service Bus Data Owner` would be one line and would also grant
// management of the namespace's entities. This API sends, receives and peeks dead letters; it has
// no business creating or deleting topics. Sender + Receiver is the same capability with none of
// the authority.
// =================================================================================================

param namespaceName string

@description('Object id of the identity being granted the roles. NOT the client id — a role assignment names the Entra object.')
param principalId string

resource sbNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: namespaceName
}

// 69a216fc-b8fb-44d8-bc22-1f3c2cd27a39 — Azure Service Bus Data Sender
// 4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0 — Azure Service Bus Data Receiver
//
// Hard-coded because role definition GUIDs are stable across every Azure tenant, and resolving
// them by name at deploy time needs a read over the role-definition list that a least-privilege
// deployer should not hold.
var roleDefinitionIds = [
  '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
]

resource assignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for roleId in roleDefinitionIds: {
  scope: sbNamespace

  // Deterministic over (scope, principal, role): a redeploy is idempotent instead of failing with
  // RoleAssignmentExists, and two principals on one namespace stay distinct instead of colliding.
  name: guid(sbNamespace.id, principalId, roleId)

  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
    principalId: principalId

    // Without this, a deployment that runs before Entra has replicated the new identity fails with
    // "principal does not exist". Declaring the type skips the directory lookup entirely.
    principalType: 'ServicePrincipal'
  }
}]

output fullyQualifiedNamespace string = '${sbNamespace.name}.servicebus.windows.net'
