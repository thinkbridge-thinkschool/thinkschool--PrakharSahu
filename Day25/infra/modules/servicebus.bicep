// =============================================================================================
// Service Bus — local auth disabled. The SAS keys still exist and are worth nothing.
//
// ---------------------------------------------------------------------------------------------
// WHY `disableLocalAuth` IS STRONGER THAN "WE DO NOT USE THE CONNECTION STRING"
//
// Every Service Bus namespace is created with a RootManageSharedAccessKey rule whether you want
// it or not. Not using it is a convention; conventions are what get broken by the person who
// needs a queue working before a demo and finds `az servicebus namespace authorization-rule
// keys list` in a search result.
//
// `disableLocalAuth: true` makes the namespace REJECT those keys. The rule is still listed, the
// key can still be read by anyone with management rights, and a client presenting it is refused:
//
//   Unauthorized access. 'Send' claim(s) are required to perform this operation.
//
// So the failure mode changes from "somebody quietly uses a shared secret and nobody notices"
// to "it does not work, immediately, for everyone". A control that fails loudly is the only kind
// that survives a deadline.
// =============================================================================================

param location string
param namespaceName string
param queueName string
param tags object

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: namespaceName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    // The line this module exists for.
    disableLocalAuth: true

    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

// ---------------------------------------------------------------------------------------------
// One queue, used as an identity probe rather than as a real topology.
//
// Day 23 built the genuine fan-out — a topic, one subscription per consuming module, a SQL
// filter on each. None of that is repeated here, because this queue exists to answer one
// question: can the app send and receive with nothing but a token? A round trip through a queue
// answers it in a way that a successful connection alone does not, since Service Bus will accept
// a connection and then refuse the operation.
// ---------------------------------------------------------------------------------------------
resource queue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: namespace
  name: queueName
  properties: {
    maxDeliveryCount: 3
    defaultMessageTimeToLive: 'PT10M'
    lockDuration: 'PT30S'
    deadLetteringOnMessageExpiration: true
  }
}

output namespaceName string = namespace.name
output queueName string = queue.name
output fullyQualifiedNamespace string = '${namespace.name}.servicebus.windows.net'
