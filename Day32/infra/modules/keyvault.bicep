// =================================================================================================
// The vault, and the one thing in it.
//
// This deployment has exactly one secret. Naming it is worth doing, because the interesting claim
// of the whole stack is how few there are:
//
//   jwt-signing-key       signs and validates every access token the Quotes API issues. Anyone
//                         holding it can mint a token for any user, including an administrator.
//
// There used to be a second: a Service Bus SAS connection string. It is gone, and its absence is
// the more interesting fact. The namespace has local auth disabled, so the SAS key was rejected on
// every use; the fix was not a better place to keep the credential but to stop having one, and
// reach the broker with the same managed identity that reaches SQL. See modules/servicebus-rbac.bicep.
//
// Everything else the four applications need — tenant ids, client ids, hostnames, audiences,
// database names — is a public identifier and is set as a plain environment variable on purpose.
// Treating those as secrets would be cargo cult: it would hide values that are printed in the
// portal anyway, and it would dilute the signal that this one is different.
//
// Not used here, and deliberately: Key Vault's certificate and key stores. The applications do no
// signing or TLS termination of their own — Container Apps terminates TLS with a platform-managed
// certificate — so a vault holding one secret is the entire requirement.
// =================================================================================================

param name string
param location string

@description('Object id of the identity allowed to READ secrets. Read, never write: an application that can rewrite its own signing key can lock every user out and leave no trace of who did it.')
param readerPrincipalId string

@description('Object id of the human running the deployment. Needs data-plane access so a redeploy can READ BACK the existing signing key instead of minting a new one -- see the role assignment below for why that is not automatic.')
param operatorPrincipalId string

@secure()
param jwtSigningKey string

param tags object

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }

    // RBAC rather than the legacy access-policy model. Access policies live on the vault, so who
    // can read a secret is invisible from the identity's side and invisible to any subscription-
    // wide access review. RBAC assignments show up in both places.
    enableRbacAuthorization: true

    // Soft delete is mandatory and cannot be turned off. Seven days is the minimum, chosen so that
    // a teardown followed by a redeploy is not blocked for a fortnight by a name that is gone but
    // not yet purgeable. Day 27 lost time to exactly that: "vault name is not available" for a
    // vault that had already been deleted.
    enableSoftDelete: true
    softDeleteRetentionInDays: 7

    // Purge protection OFF, on purpose and against the usual advice. With it on, a deleted vault
    // cannot be purged early by anyone, including the owner, and the name is unusable for the full
    // retention window. That is correct for production, where an attacker deleting a vault must
    // not also be able to destroy it. It is wrong here, where teardown and redeploy is the whole
    // operating model and the contents are one regenerable value.
    enablePurgeProtection: null

    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource jwtSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'jwt-signing-key'
  properties: {
    value: jwtSigningKey
    contentType: 'HMAC-SHA256 signing key, base64'
  }
}

// 4633458b-17de-408a-b874-0445c86b69e6 — Key Vault Secrets User. Read secret CONTENTS, and nothing
// else: it cannot list vaults, cannot write, cannot manage access. The broader "Key Vault Reader"
// is a common and wrong choice here — it grants metadata, not values, so it produces a 403 at
// runtime that reads like a bug in the application.
var secretsUserRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '4633458b-17de-408a-b874-0445c86b69e6'
)

resource readSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, readerPrincipalId, secretsUserRoleDefinitionId)
  properties: {
    roleDefinitionId: secretsUserRoleDefinitionId
    principalId: readerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// -------------------------------------------------------------------------------------------------
// The operator, and the Azure behaviour that is easy to get wrong.
//
// Owner on the subscription does NOT grant access to a vault's CONTENTS. The management plane and
// the data plane are separate: Owner lets you delete the vault and cannot read a secret out of it.
// With enableRbacAuthorization, reading a value needs an explicit data-plane role.
//
// Without this assignment the failure is silent rather than loud. deploy.sh tries to read the
// existing jwt-signing-key so a redeploy does not rotate it; the read returns nothing; the script
// takes its "no key yet" branch and mints a fresh one. Every token every signed-in user holds is
// invalidated, the deployment reports success, and the only visible symptom is that people are
// mysteriously logged out after each release.
//
// Officer rather than User, because the deploy needs to WRITE the secret on a first run and READ it
// on every subsequent one.
//
// 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7' — Key Vault Secrets Officer, verified with
// `az role definition list --name "Key Vault Secrets Officer"`. Hard-coding these GUIDs is
// correct, since they are identical in every Azure tenant, but a wrong one fails only at
// deploy time with RoleDefinitionDoesNotExist. Worth checking rather than recalling: the
// first attempt here had the right first half and an invented second.
// -------------------------------------------------------------------------------------------------
var secretsOfficerRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
)

resource operatorSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, operatorPrincipalId, secretsOfficerRoleDefinitionId)
  properties: {
    roleDefinitionId: secretsOfficerRoleDefinitionId
    principalId: operatorPrincipalId

    // A human, not a service principal. Declaring it wrong makes ARM skip the directory lookup and
    // create an assignment against a principal type that does not match, which resolves to nothing.
    principalType: 'User'
  }
}

output name string = vault.name

// The versionless URI. A version-pinned one would freeze the apps on today's value, so rotating a
// secret would silently do nothing until every container app was redeployed — which is the failure
// mode where rotation appears to work and does not.
output jwtSecretUri string = '${vault.properties.vaultUri}secrets/jwt-signing-key'
