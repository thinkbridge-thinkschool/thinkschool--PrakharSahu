// =============================================================================================
// The user-assigned managed identity. The single principal every other module grants access to.
//
// ---------------------------------------------------------------------------------------------
// WHAT A MANAGED IDENTITY ACTUALLY IS, since "it replaces the password" hides the mechanism
//
// It is an Entra service principal whose credential Azure holds and rotates, and which the
// platform hands to the compute resource over a link nothing else can use. Inside the App
// Service, code asks a local endpoint for a token:
//
//   GET http://169.254.169.254/metadata/identity/oauth2/token?resource=https://database.windows.net
//   X-IDENTITY-HEADER: <per-instance value injected by the platform>
//
// That address is link-local — it is not routable, so the request cannot leave the machine and
// cannot arrive from outside it. The response is a JWT valid for roughly 24 hours.
//
// The property that matters: there is no long-lived secret at either end. The app never holds
// one, so it cannot leak one, and a token exfiltrated from a compromised process expires on its
// own. Compare a connection-string password, which is valid until a human notices and rotates it.
// =============================================================================================

param location string
param identityName string
param tags object

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

// ---------------------------------------------------------------------------------------------
// Three ids, three different jobs. Confusing them is the most common managed-identity mistake.
//
//   resourceId   the ARM path. Used to ATTACH the identity to the App Service, and to tell the
//                site which identity to use when resolving Key Vault references.
//
//   principalId  the Entra OBJECT id. This is what a role assignment grants to, and what SQL
//                matches when the identity connects. Never appears in application code.
//
//   clientId     the Entra APPLICATION id. This is what CODE passes, because a resource can
//                carry several user-assigned identities and the token endpoint has to be told
//                which one is being asked for. Omit it and DefaultAzureCredential fails with an
//                error about multiple identities that names none of them.
// ---------------------------------------------------------------------------------------------
output resourceId string = identity.id
output principalId string = identity.properties.principalId
output clientId string = identity.properties.clientId
output name string = identity.name
