// A user-assigned managed identity, and the three ids it has.
//
// Three, which is the trap. `id` is the ARM resource id and is what a container app references.
// `principalId` is the Entra object id and is what a role assignment names. `clientId` is the
// application id and is what SQL derives a user's SID from, and what a connection string carries
// as `User Id`.
//
// Handing SQL the principal id instead of the client id is the single most expensive mistake in
// this repository's history: the CREATE USER succeeds, the app authenticates successfully, and
// the login is still refused with `Login failed for user '<token-identified principal>'` —
// because the user is real and the token is real and their SIDs do not match. All three are
// emitted here so no caller has to guess which one it wanted.

param name string
param location string
param tags object

resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: name
  location: location
  tags: tags
}

output name string = uami.name
output resourceId string = uami.id
output principalId string = uami.properties.principalId
output clientId string = uami.properties.clientId
