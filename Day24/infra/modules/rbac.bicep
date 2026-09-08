// =============================================================================================
// Role assignments granting the API's managed identity access to Service Bus.
//
// ---------------------------------------------------------------------------------------------
// WHY THIS IS A SEPARATE MODULE, and not four lines in main.bicep
//
// It was four lines in main.bicep first, and it did not compile:
//
//   BCP120: This expression is being used in an assignment to the "scope" property of the
//   "Microsoft.Authorization/roleAssignments" type, which requires a value that can be
//   calculated at the START of the deployment.
//
// A role assignment's `scope` and `name` are part of the resource's identity, so ARM has to know
// them before it submits anything. `serviceBus.outputs.namespaceName` and
// `api.outputs.principalId` are only known once those modules have RUN, so neither can appear
// there.
//
// A module boundary fixes it because a module's PARAMETERS are, by definition, known at the
// start of that module's own deployment. The values still arrive at runtime from the caller;
// they are simply resolved one deployment earlier. That is the whole trick, and it is the
// standard way to express "grant this identity access to that resource" in Bicep.
// =============================================================================================

@description('Name of the Service Bus namespace to grant access on.')
param serviceBusNamespaceName string

@description('Object id of the managed identity being granted access.')
param principalId string

// Well-known built-in role definition ids. Hard-coded because they are stable platform
// constants, not configuration — looking them up at deploy time would add a failure mode and
// save nothing.
var serviceBusDataSenderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
var serviceBusDataReceiverRoleId = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName
}

// ---------------------------------------------------------------------------------------------
// Sender AND receiver, as two assignments rather than one `Azure Service Bus Data Owner`.
//
// Dispatch publishes work-order events and consumes them on two subscriptions, so it genuinely
// needs both. Owner would also grant manage rights — creating and deleting topics — which the
// application never uses. A role granting more than the workload needs is the same mistake as an
// over-scoped secret, and it is harder to notice because nothing fails.
// ---------------------------------------------------------------------------------------------
resource senderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: namespace

  // A DETERMINISTIC name. Role assignment names must be GUIDs, and re-running the deployment
  // has to produce the same one — otherwise every deploy attempts a fresh assignment and fails
  // with RoleAssignmentExists. guid() over (scope, principal, role) is the canonical recipe:
  // same inputs, same name, idempotent deployment.
  name: guid(namespace.id, principalId, serviceBusDataSenderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataSenderRoleId)
    principalId: principalId

    // Without this, a deployment that runs before the identity has replicated through Entra
    // fails with "principal does not exist in the directory" — an error that is transient,
    // misleading, and vanishes on retry. Stating the type lets ARM skip the lookup and wait.
    principalType: 'ServicePrincipal'
  }
}

resource receiverAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: namespace
  name: guid(namespace.id, principalId, serviceBusDataReceiverRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataReceiverRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

output senderAssignmentId string = senderAssignment.id
output receiverAssignmentId string = receiverAssignment.id
