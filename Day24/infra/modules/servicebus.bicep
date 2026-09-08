// =============================================================================================
// Service Bus for the Dispatch capstone.
//
// Day 22 piece 2 ships an InProcessIntegrationEventPublisher and names it as the exit hatch:
// "Replace it with a Service Bus topic and no module changes, because none of them was ever
// allowed to know which it was." This is that topic.
//
// The topology comes straight from the design, not from a template gallery. Three integration
// events cross a module boundary in that solution, and every subscription below exists because
// a named module subscribes to it. A topic with no subscriber is a monthly bill for nothing.
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

@description('Namespace name, computed by the caller so what-if can resolve it before deployment.')
param namespaceName string

@description('Namespace SKU. Standard is the MINIMUM that supports topics — Basic gives queues only.')
@allowed([ 'Standard', 'Premium' ])
param skuName string

@description('Premium messaging units. Ignored entirely on Standard.')
@allowed([ 1, 2, 4, 8, 16 ])
param messagingUnits int

@description('How many times a message is delivered before the broker dead-letters it itself.')
@minValue(1)
@maxValue(100)
param maxDeliveryCount int

@description('How long an unconsumed message survives. ISO 8601 duration.')
param messageTimeToLive string

param tags object

// ---------------------------------------------------------------------------------------------
// The namespace.
//
// `disableLocalAuth: true` is the line that matters. It switches OFF SAS keys entirely, so the
// namespace has no connection string to copy into an app setting and nothing to rotate. Access
// is by Entra RBAC only — the role assignments live in main.bicep, granted to the API's managed
// identity.
//
// This is the same decision as Day 17's `--admin-enabled false` on the container registry: the
// convenient path writes a credential into an app setting, so the convenient path is closed.
// ---------------------------------------------------------------------------------------------
resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: namespaceName
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuName
    capacity: skuName == 'Premium' ? messagingUnits : null
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    zoneRedundant: skuName == 'Premium'
  }
}

// ---------------------------------------------------------------------------------------------
// One topic per aggregate that publishes, not one topic per event.
//
// A topic per event type multiplies infrastructure by the size of the domain and gives every
// new event a deployment. A topic per publishing aggregate keeps the count bounded and lets
// subscribers filter — which is what subscription rules are for, and why the events carry their
// type in an application property that a filter can read WITHOUT deserialising the body.
// ---------------------------------------------------------------------------------------------
resource workOrderEvents 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: namespace
  name: 'work-order-events'
  properties: {
    defaultMessageTimeToLive: messageTimeToLive
    enablePartitioning: skuName == 'Standard'
    supportOrdering: false

    // Deliberately OFF, and Day 19 explains why at length: broker dedupe only protects against
    // a PUBLISHER retrying a send inside a short window. It does nothing about the case that
    // actually matters — a consumer that did the work and died before settling. Only the
    // consumer can defend against that, and Dispatch's handlers already do.
    requiresDuplicateDetection: false
  }
}

// ---------------------------------------------------------------------------------------------
// Subscriptions. One per consuming module, each named after the module that owns it.
//
// Every subscription gets its own copy of every message, its own delivery count and its OWN
// dead-letter queue — which is the entire reason to pay for a topic rather than a queue. A
// message that poisons Billing dead-letters in Billing's DLQ while Scheduling completes the
// same event normally.
// ---------------------------------------------------------------------------------------------
var subscriptions = [
  {
    name: 'scheduling'
    description: 'Reserves a technician slot when a work order is scheduled; releases it when released.'
    // WorkOrderScheduledV1 and WorkOrderReleasedV1 — the two Scheduling reacts to.
    eventTypes: [ 'WorkOrderScheduledV1', 'WorkOrderReleasedV1' ]
  }
  {
    name: 'billing'
    description: 'Drafts an invoice when a work order is completed.'
    eventTypes: [ 'WorkOrderCompletedV1' ]
  }
]

resource topicSubscriptions 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = [
  for sub in subscriptions: {
    parent: workOrderEvents
    name: sub.name
    properties: {
      maxDeliveryCount: maxDeliveryCount
      defaultMessageTimeToLive: messageTimeToLive

      // Dead-lettering on expiry is ON: a message nobody consumed before its TTL is evidence of
      // a broken consumer, and silently discarding it destroys that evidence.
      deadLetteringOnMessageExpiration: true

      // Dead-lettering on filter-evaluation error is also ON, for the same reason. A rule that
      // throws is a deployment bug, and it should surface as messages in a DLQ rather than as
      // messages that quietly vanish.
      deadLetteringOnFilterEvaluationExceptions: true

      // Locks are long enough for a handler that has to touch SQL, and renewable beyond it.
      lockDuration: 'PT1M'
    }
  }
]

// ---------------------------------------------------------------------------------------------
// Subscription filters.
//
// Without a rule, a subscription receives EVERY message on the topic and each consumer has to
// deserialise and discard what it does not want — paying delivery cost for messages it will
// never act on. The filter reads `eventType` from the application properties, which Dispatch's
// publisher sets precisely so this is possible without touching the body.
// ---------------------------------------------------------------------------------------------
resource subscriptionRules 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2022-10-01-preview' = [
  for (sub, i) in subscriptions: {
    parent: topicSubscriptions[i]
    name: 'only-relevant-events'
    properties: {
      filterType: 'SqlFilter'
      sqlFilter: {
        sqlExpression: 'eventType IN (${join(map(sub.eventTypes, t => '\'${t}\''), ', ')})'
      }
    }
  }
]

output namespaceName string = namespace.name
output namespaceId string = namespace.id
output fullyQualifiedNamespace string = '${namespace.name}.servicebus.windows.net'
output topicName string = workOrderEvents.name
output subscriptionNames array = [for sub in subscriptions: sub.name]
