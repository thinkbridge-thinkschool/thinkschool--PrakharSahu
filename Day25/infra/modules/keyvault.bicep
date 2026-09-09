// =============================================================================================
// Key Vault — for the credentials that CANNOT be replaced by an identity.
//
// ---------------------------------------------------------------------------------------------
// WHAT KEY VAULT IS AND IS NOT FOR HERE
//
// Key Vault is not the answer to "where do we put the SQL password". The answer to that is to
// stop having one, which is what Entra-only auth and managed identity accomplish. A vault
// holding a SQL password is a smaller version of the same problem: the credential still exists,
// still never rotates on its own, and now there is one more hop that can be misconfigured.
//
// What a vault is genuinely for is the leftovers — a third party that only issues shared
// secrets and does not federate. This vault holds exactly one, and the app reads it through a
// Key Vault REFERENCE so the value never lands in an app setting.
//
// ---------------------------------------------------------------------------------------------
// TWO CHOICES THAT DO THE REAL WORK
//
// `enableRbacAuthorization: true` — RBAC rather than the legacy access-policy model. Access
// policies are per-vault, invisible to `az role assignment list`, and cannot be reasoned about
// from outside the vault. RBAC puts vault access in the same place as every other permission in
// the subscription, which is the only way an audit can be complete.
//
// `enablePurgeProtection: true` — a deleted secret cannot be permanently erased before its
// retention window elapses. This is the setting people disable because it makes teardown
// awkward, and it is the one that means a mistaken delete is recoverable rather than terminal.
//
// NOTE: purge protection is IRREVERSIBLE once enabled, and it outlives the resource group. A
// vault name cannot be reused until the retention window expires, which is why the name carries
// a suffix derived from the resource group id rather than being a fixed string.
// =============================================================================================

param location string
param keyVaultName string
param tags object

@description('Soft-delete retention, days. 7 is the minimum Azure permits.')
@minValue(7)
@maxValue(90)
param softDeleteRetentionInDays int = 7

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId

    // RBAC, not access policies. See the header.
    enableRbacAuthorization: true

    // Recoverability. Both on, deliberately.
    enableSoftDelete: true
    softDeleteRetentionInDays: softDeleteRetentionInDays
    enablePurgeProtection: true

    // Deployment integration left OFF. A vault that ARM may read from is a vault whose secrets
    // can be surfaced in a deployment's parameters and therefore in its history. Nothing here
    // needs it, because no secret is passed to a template at all.
    enabledForTemplateDeployment: false
    enabledForDeployment: false
    enabledForDiskEncryption: false

    // Public network access stays on, and that is a real gap rather than an oversight — see
    // EXERCISE.md. The vault is protected by Entra RBAC, not by network position, but defence in
    // depth would put a private endpoint in front of it and this does not.
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

// ---------------------------------------------------------------------------------------------
// There is deliberately NO `Microsoft.KeyVault/vaults/secrets` resource in this file.
//
// Creating the secret here would mean its value arrives as a template parameter, and a parameter
// is recorded in the deployment history whether or not it is marked `@secure()` — secure values
// are redacted in the portal, not absent from the request that carried them. It would also mean
// the value existed in a shell variable, a script argument, and a CI log on the way in.
//
// So the template creates the CONTAINER and the script fills it, using `az keyvault secret set`,
// which writes to the data plane and never touches ARM. The value exists in exactly two places:
// the vault, and the memory of the process that generated it.
// ---------------------------------------------------------------------------------------------

output vaultUri string = keyVault.properties.vaultUri
output name string = keyVault.name
output id string = keyVault.id
