// =============================================================================================
// RBAC — the module that replaces every connection string this deployment does not have.
//
// ---------------------------------------------------------------------------------------------
// WHY THIS IS A SEPARATE MODULE
//
// A role assignment's NAME and SCOPE must both be computable before the deployment starts. Build
// either from a value that only exists once another resource has been created and Bicep refuses
// to compile it:
//
//   BCP120: This expression is being used in an assignment to the "name" property of the
//   "Microsoft.Authorization/roleAssignments" type, which requires a value that can be calculated
//   at the start of the deployment.
//
// Module PARAMETERS are start-known by definition, so moving the assignments behind a module
// boundary satisfies the compiler without weakening anything. Day 23 found this the hard way.
//
// ---------------------------------------------------------------------------------------------
// WHY THE ROLES ARE THE NARROW ONES
//
// Every assignment below is the least-privilege built-in for the operation the app actually
// performs. The broader roles are one word away and each one grants something the workload never
// uses:
//
//   Key Vault Secrets User    vs  Key Vault Administrator     — admin can write and delete every
//                                                               secret in the vault, and manage
//                                                               access to it
//   Service Bus Data Sender   vs  Service Bus Data Owner      — owner can also create and delete
//   Service Bus Data Receiver     (both, in one role)           queues and read every key
//
// An over-scoped role is the same mistake as an over-scoped secret. It is worse in one respect:
// a leaked secret is at least visibly a credential, while an over-broad role looks like
// configuration and survives review.
// =============================================================================================

param keyVaultName string
param serviceBusNamespaceName string

@description('Entra OBJECT id of the managed identity — not its client id.')
param principalId string

// Well-known built-in role definition ids. Hard-coded because they are stable platform
// constants, identical in every tenant. Looking them up at deploy time would add a failure mode
// to save nothing.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var serviceBusDataSenderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
var serviceBusDataReceiverRoleId = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName
}

// ---------------------------------------------------------------------------------------------
// `guid(scope, principal, role)` produces a name that is deterministic and unique.
//
// Deterministic matters more than it looks: redeploying must produce the SAME name, or every
// deployment creates a duplicate assignment and the list grows without bound. A hand-typed GUID
// would also be stable, and would be one copy-paste away from silently overwriting a different
// assignment.
// ---------------------------------------------------------------------------------------------

resource keyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, principalId, keyVaultSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: principalId

    // Without `principalType`, ARM looks the principal up in Entra to decide what it is. A
    // freshly created identity may not have replicated yet, and the deployment fails with
    // "PrincipalNotFound" on a principal that plainly exists. Stating the type skips the lookup.
    principalType: 'ServicePrincipal'
  }
}

resource serviceBusSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBusNamespace
  name: guid(serviceBusNamespace.id, principalId, serviceBusDataSenderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataSenderRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

resource serviceBusReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBusNamespace
  name: guid(serviceBusNamespace.id, principalId, serviceBusDataReceiverRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataReceiverRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

output keyVaultSecretsUserAssignmentId string = keyVaultSecretsUser.id
output serviceBusSenderAssignmentId string = serviceBusSender.id
output serviceBusReceiverAssignmentId string = serviceBusReceiver.id
