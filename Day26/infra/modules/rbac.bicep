// =============================================================================================
// Service Bus data roles for the identity that runs the application.
//
// A separate module for the reason Day 23 discovered and Day 25 reused: a role assignment's name
// and scope must both be computable before the deployment starts, or Bicep refuses with BCP120.
// Module parameters are start-known by definition, so a module boundary satisfies the compiler
// without weakening anything.
// =============================================================================================

param serviceBusNamespaceName string

@description('Entra OBJECT id of the principal — not its client id.')
param principalId string

@description('User for a developer running locally; ServicePrincipal for a workload identity.')
@allowed([ 'User', 'ServicePrincipal' ])
param principalType string = 'User'

// Well-known built-in role ids. Stable platform constants, identical in every tenant, so they are
// hard-coded rather than looked up — a lookup would add a failure mode and save nothing.
var dataSenderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
var dataReceiverRoleId = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName
}

// Sender and Receiver as two assignments rather than one Azure Service Bus Data Owner. Owner also
// grants manage rights — creating and deleting queues, reading authorization keys — which this
// workload never uses. Splitting them also makes the pair auditable: it is visible at a glance
// that the app can send and receive and cannot reconfigure the namespace.
resource sender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: namespace
  name: guid(namespace.id, principalId, dataSenderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', dataSenderRoleId)
    principalId: principalId
    principalType: principalType
  }
}

resource receiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: namespace
  name: guid(namespace.id, principalId, dataReceiverRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', dataReceiverRoleId)
    principalId: principalId
    principalType: principalType
  }
}

output senderAssignmentId string = sender.id
output receiverAssignmentId string = receiver.id
