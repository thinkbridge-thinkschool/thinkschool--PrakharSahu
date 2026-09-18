// =================================================================================================
// Azure Cache for Redis — the L2 behind HybridCache.
//
// WHY THERE IS AN L2 AT ALL
//
// HybridCache already gives L1: an in-process memory cache. L1 is per-replica, which means two
// things that only matter once something is deployed. Every replica keeps its own copy, so a cache
// entry invalidated on one is still being served by the others; and a replica that restarts starts
// cold. With minReplicas: 0 this deployment restarts constantly, so L1 alone is a cache that is
// almost always empty exactly when it is needed.
//
// L2 is shared and survives a restart. HybridCache discovers it through the container — register an
// IDistributedCache and it is used automatically, with no "use Redis" call anywhere.
//
// ---------------------------------------------------------------------------------------------
// ENTRA AUTHENTICATION, WHICH IS THE WHOLE POINT OF THIS FILE
//
// The default way to reach Azure Cache for Redis is an access key, and the default place to put it
// is a connection string. That would have been three lines and no code change — and it would have
// added the second secret to a deployment whose most interesting property is that it holds one.
//
// Redis supports Entra token authentication, so the same managed identity that reaches SQL and
// Service Bus reaches the cache. Two things make that work, and both are here:
//
//   redisConfiguration['aad-enabled']  turns token auth on for the cache
//   accessPolicyAssignments            maps a principal to a Redis ACL rule
//
// There is no ARM role for Redis data access. The access policy assignment below IS the grant, and
// it is a child of the cache rather than a roleAssignment — which is why it looks different from
// every other permission in this template.
// =================================================================================================

param name string
param location string

@description('Object id of the identity allowed to use the cache.')
param principalId string

@description('A human-readable name for the principal, required by the access policy assignment. Not used for authorization — the object id is.')
param principalName string

param tags object

resource cache 'Microsoft.Cache/redis@2024-11-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    sku: {
      // Basic C0 — 250 MB, single node, NO SLA. That is the honest description, and it is the
      // right tier here: this holds a cache of a quotes list. Losing it costs one database query.
      // Standard would double the price to buy a replica for data that is by definition
      // reconstructible, which is the wrong thing to pay for.
      name: 'Basic'
      family: 'C'
      capacity: 0
    }

    redisVersion: '6'

    // TLS only, and 1.2 as the floor. `enableNonSslPort: false` is the default and is restated
    // because the non-TLS port is a single boolean away and carries the token in clear text.
    enableNonSslPort: false
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'

    redisConfiguration: {
      // Token authentication. Without this the access policy assignment below deploys
      // successfully and does nothing, because the cache is still only willing to check keys.
      'aad-enabled': 'True'

      // Evict the least recently used key when full rather than refusing writes. For a cache this
      // is the only sane policy: `noeviction`, the Redis default, turns a full cache into an
      // application error instead of a cache miss.
      'maxmemory-policy': 'allkeys-lru'
    }
  }
}

// -------------------------------------------------------------------------------------------------
// The grant.
//
// 'Data Contributor' is one of three built-in Redis access policies (Data Owner, Data Contributor,
// Data Reader). Contributor can read and write keys and cannot run administrative commands — it
// cannot FLUSHALL the cache or reconfigure it. Owner would allow both, and nothing here needs it.
// -------------------------------------------------------------------------------------------------
resource accessPolicy 'Microsoft.Cache/redis/accessPolicyAssignments@2024-11-01' = {
  parent: cache
  name: 'quotes-api'
  properties: {
    accessPolicyName: 'Data Contributor'
    objectId: principalId
    objectIdAlias: principalName
  }
}

output name string = cache.name
output hostName string = cache.properties.hostName

// Host and port, no credential. The client appends nothing to this: the token is fetched at
// connection time by the identity named above. Safe to emit as a plain output for the same reason
// the SQL connection string is safe to print — there is nothing in it worth stealing.
output endpoint string = '${cache.properties.hostName}:${cache.properties.sslPort}'
