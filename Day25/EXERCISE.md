# Day 25 — Identity end-to-end

No connection-string secrets anywhere. Managed identity for API→SQL and API→Service Bus, Entra ID
for app auth, Key Vault references for the config that genuinely cannot lose its credential.

## Result

| | |
|---|---:|
| App settings examined | **10** |
| App settings containing a credential | **0** |
| Entries in the App Service connection-strings slot | **0** |
| `secureString` parameters across all deployments | **0** |
| Client secrets / certificates on the app registration | **0 / 0** |
| Roles held by the managed identity | **3, all narrow** |
| Zero-secrets proof | **12 passed, 0 failed** |
| Live identity paths working | **3 of 3** |

Captured: [`docs/no-secrets-proof.txt`](docs/no-secrets-proof.txt) ·
[`docs/identity-probes.txt`](docs/identity-probes.txt) ·
[`docs/sql-grant.txt`](docs/sql-grant.txt) ·
[`docs/deploy.txt`](docs/deploy.txt)

The claim is not "we store our secrets carefully". It is that **there is nothing to store**, and
that a secret would be useless even if one leaked.

| path | how it authenticates | what does not exist |
|---|---|---|
| API → SQL | managed identity + Entra-only server | no password, and none can be set |
| API → Service Bus | managed identity + `disableLocalAuth` | SAS keys exist and are **rejected** |
| caller → API | Entra ID, token validation only | no client secret, no certificate |
| third-party config | Key Vault reference | the value is never in app settings |

---

## 1. The MI wiring

### Create the identity first, grant it everything, then create the app

The ordering is the design, and it is why this uses a **user-assigned** identity rather than the
simpler system-assigned one used on Days 23 and 24.

A system-assigned identity does not exist until its host resource does, so every grant must come
*after* the app. Key Vault references resolve at **startup** — during that same deployment, before
the grant exists. The app boots, is refused, and caches a failed reference.

A user-assigned identity is an independent resource, so `infra/main.bicep` can order it properly:

```bicep
module identity 'modules/identity.bicep' = { ... }        // 1. the principal

module rbac 'modules/rbac.bicep' = {                      // 2. every grant it will need
  params: { principalId: identity.outputs.principalId }
  dependsOn: [ keyVault, serviceBus ]
}

module app 'modules/app.bicep' = {                        // 3. the app, last
  params: {
    managedIdentityId: identity.outputs.resourceId
    managedIdentityClientId: identity.outputs.clientId
  }
  dependsOn: [ rbac ]                                     // <-- the whole argument, in one line
}
```

### Attaching it, and the line everyone forgets

```bicep
resource site 'Microsoft.Web/sites@2023-12-01' = {
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${managedIdentityId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true

    // Required, not optional, for a user-assigned setup. The default is the SYSTEM-assigned
    // identity, and this site does not have one — leave it out and every Key Vault reference
    // fails with an access error naming a principal that does not exist.
    keyVaultReferenceIdentity: managedIdentityId
```

### API → SQL

```bicep
var sqlConnectionString = join([
  'Server=tcp:${sqlServerFqdn},1433'
  'Initial Catalog=${sqlDatabaseName}'
  'Encrypt=True'
  'TrustServerCertificate=False'
  'Connection Timeout=60'
  'Authentication=Active Directory Managed Identity'
  'User Id=${managedIdentityClientId}'
], ';')
```

Two lines carry it. `Authentication=Active Directory Managed Identity` tells the driver to fetch a
token instead of authenticating with anything supplied here. `User Id=<clientId>` is **not** a
username — with a user-assigned identity it names *which* identity to request a token for, because
a resource can carry several and the token endpoint will not guess. Omit it and you get
`ManagedIdentityCredential authentication failed`, which reads like a permissions problem and is an
ambiguity problem.

The server side is what makes the absence real:

```bicep
administrators: {
  administratorType: 'ActiveDirectory'
  login: sqlAdminLogin
  sid: sqlAdminObjectId
  azureADOnlyAuthentication: true        // password auth is REFUSED, not merely unused
}
```

### API → Service Bus

```bicep
resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  properties: {
    disableLocalAuth: true               // the RootManageSharedAccessKey is rejected
    minimumTlsVersion: '1.2'
  }
}
```

Every namespace is created with a `RootManageSharedAccessKey` whether you want one or not. Not
using it is a *convention*, and conventions are what break when somebody needs a queue working
before a demo. `disableLocalAuth: true` makes the namespace reject those keys, so the failure mode
changes from "somebody quietly uses a shared secret" to "it does not work, immediately, for
everyone".

### The roles, and why they are the narrow ones

```bicep
var keyVaultSecretsUserRoleId    = '4633458b-17de-408a-b874-0445c86b69e6'
var serviceBusDataSenderRoleId   = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
var serviceBusDataReceiverRoleId = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'

resource keyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, principalId, keyVaultSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'    // skips an Entra lookup that fails on a fresh identity
  }
}
```

`Key Vault Secrets User`, not `Key Vault Administrator` — admin also grants write and delete on
every secret in the vault. Sender and Receiver separately, not `Azure Service Bus Data Owner` —
owner also grants manage. An over-scoped role is the same mistake as an over-scoped secret, and
worse in one respect: a leaked secret is at least visibly a credential, while an over-broad role
looks like configuration and survives review.

### In the application, one credential object holding nothing

```csharp
var managedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"];

var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ManagedIdentityClientId = managedIdentityClientId
});
```

Registered as a singleton, because `DefaultAzureCredential` caches tokens internally and a
per-request instance throws that cache away — turning a 2 ms operation into a network round trip
and risking Entra throttling.

---

## 2. A Key Vault reference

```bicep
var thirdPartySecretReference = '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=${thirdPartySecretName})'

appSettings: [
  {
    name: 'ThirdParty__WebhookSigningKey'
    value: thirdPartySecretReference
  }
]
```

Which lands on the site as, verbatim:

```
ThirdParty__WebhookSigningKey  =  @Microsoft.KeyVault(VaultName=kv-idty-dev-sesxqu;SecretName=payments-webhook-signing-key)
```

App Service resolves this at startup: it takes a token for the vault using the identity named by
`keyVaultReferenceIdentity`, reads the secret, and injects the **value** into the process
environment. The app reads an ordinary environment variable and never knows a vault was involved.

**No version is pinned.** `SecretName=x` without `;SecretVersion=y` follows the current version,
which is what makes rotation a vault-only operation. Pinning a version is the safer-sounding choice
and means every rotation needs a redeployment — which is how secrets end up un-rotated.

**The template never sees the value.** There is deliberately no
`Microsoft.KeyVault/vaults/secrets` resource in `keyvault.bicep`. Creating the secret in Bicep
would mean its value arrives as a parameter, and a parameter is recorded in the deployment history
whether or not it is marked `@secure()` — secure values are *redacted in the portal*, not absent
from the request that carried them. So the template creates the container and `deploy.sh` fills it
via `az keyvault secret set`, which writes to the vault's data plane and never touches ARM.

The platform's own verdict on whether it worked:

```
  ThirdParty__WebhookSigningKey
    status      : Resolved
    detail      : Reference has been successfully resolved.
    resolved by : UserAssigned identity
    vault/secret: kv-idty-dev-sesxqu / payments-webhook-signing-key
```

**The honest limit.** Once resolved, the value *is* in the process environment, so anything that
can read the app's memory or run code inside it can read the secret. Key Vault references remove
the secret from configuration and from source control. They do not make a compromised process
safe, and no configuration setting can.

---

## 3. The app settings have no plaintext secrets

Every setting on the site, classified. Nothing is skipped — an unclassified value is an unreviewed
value, so the classifier treats anything it does not recognise as a failure.

```
   * APPLICATIONINSIGHTS_CONNECTION_STRING  [TELEMETRY INGEST]
                                             InstrumentationKey=<guid>;...  write-only ingestion id
     ASPNETCORE_ENVIRONMENT                 [ENDPOINT/NAME]
     AZURE_CLIENT_ID                        [IDENTIFIER]
     ConnectionStrings__Sql                 [CONN STR (no credential)]
                                             Server=tcp:sql-identity-dev-sesxqu.database.windows.net,1433;
                                             Initial Catalog=identity;Encrypt=True;TrustServerCertificate=False;
                                             Connection Timeout=60;
                                             Authentication=Active Directory Managed Identity;
                                             User Id=5d66010b-dab6-4e7e-a2ce-53f86609f561
     Entra__ClientId                        [IDENTIFIER]
     Entra__TenantId                        [IDENTIFIER]
     KeyVault__Name                         [ENDPOINT/NAME]
     ServiceBus__FullyQualifiedNamespace    [ENDPOINT/NAME]
     ServiceBus__QueueName                  [ENDPOINT/NAME]
     ThirdParty__WebhookSigningKey          [KEY VAULT REF]
                                             @Microsoft.KeyVault(VaultName=kv-idty-dev-sesxqu;SecretName=payments-webhook-signing-key)

  10 settings examined; * = noted, ? = needs a look, ! = secret
  PASS  no app setting contains a credential
  PASS  no connection string carries a credential
  PASS  every setting was classified
```

There are only three kinds of value there, and only one of them would be a secret:

**Endpoints and identifiers.** A hostname, a database name, a queue name, a client id. Public by
design — a client id ships in the JavaScript bundle of every SPA that signs in with Entra. Treating
these as secrets costs real effort, protects nothing, and dilutes the word for the values that are.

**A connection string carrying no credential.** It ends
`Authentication=Active Directory Managed Identity`, naming an auth *method* rather than supplying
one. Printing it in a log reveals a hostname somebody still cannot connect to.

**A pointer to the one genuine secret**, which is therefore not in app settings at all.

### Absence is the weak half of the proof. This is the strong half

Grepping for the word "password" proves nothing. What matters is that a secret would be *unusable*:

```
--- 2. The connection strings slot ---
  entries: 0
  PASS  the App Service connection-strings slot is empty

--- 4. Password auth is not merely unused, it is refused ---
  SQL azureADOnlyAuthentication : true
  PASS  SQL refuses password authentication outright
  Service Bus disableLocalAuth  : true
  PASS  Service Bus rejects its own SAS keys
  Key Vault RBAC authorization  : true
  PASS  vault access is RBAC, so it is auditable subscription-wide

--- 5. The Entra app registration has no credentials ---
  passwordCredentials (client secrets): 0
  keyCredentials (certificates)       : 0
  PASS  the app registration has no client secret and no certificate
  Easy Auth clientSecretSettingName    : none
  Easy Auth unauthenticatedClientAction: Return401
  PASS  Easy Auth validates tokens without a client secret

--- 6. Least privilege on the managed identity ---
    Azure Service Bus Data Receiver
    Azure Service Bus Data Sender
    Key Vault Secrets User
  PASS  no broad or administrative role is assigned to the identity

--- 7. No secret ever passed through ARM ---
  secureString parameters across all deployments in this group: 0
  PASS  no secret was passed to a deployment, so none is in its history
```

Note the empty **connection-strings slot** specifically. That is the App Service feature built for
storing connection strings — the obvious place to paste a password. It exists, and nothing is in it.

### Why Entra app auth needs no client secret

App Service Authentication can do two different things, and only one of them requires a secret:

- **Issuing** tokens — redirect an unauthenticated browser to Entra, receive an authorization code,
  exchange it for a token. That exchange requires the app to prove it is itself, with a client
  secret.
- **Validating** tokens — the caller presents a token; check its signature against Entra's
  published keys, its issuer, and its audience. All three are public information.

`unauthenticatedClientAction: 'Return401'` selects the second. No redirect is ever issued, so no
code exchange happens, so no client secret is required. For an API that is also simply correct: a
JSON client handles 401 and cannot meaningfully follow a login redirect.

```bicep
identityProviders: {
  azureActiveDirectory: {
    enabled: true
    registration: {
      openIdIssuer: '${environment().authentication.loginEndpoint}${tenantId}/v2.0'
      clientId: entraClientId
      // clientSecretSettingName is ABSENT — the line whose absence is the deliverable
    }
    validation: {
      allowedAudiences: [ 'api://${entraClientId}', entraClientId ]
    }
  }
}
```

`allowedAudiences` is not decoration. A token minted for a *different* application is signed by the
same tenant and is still not for this API; without the audience check it would be accepted, which
is the classic confused-deputy hole.

---

## 4. It actually works — the live proof

Configuration that looks right and does not work proves nothing, so there is a deployed app whose
only job is to exercise each path and report what happened.

**Easy Auth is enforced by the platform, before app code runs:**

```
  GET /health     -> HTTP 200   (excluded from auth on purpose)
  GET /whoami     -> HTTP 401
  GET /probe/sql  -> HTTP 401
```

**API → SQL.** The strongest single piece of evidence here, because `SUSER_SNAME()` is the
principal *the server itself* believes is connected:

```json
{
  "ok": true,
  "elapsedMs": 21661,
  "connectedAs": "5d66010b-dab6-4e7e-a2ce-53f86609f561@8d46a076-d093-416d-a57b-8692cde13bf8",
  "database": "identity",
  "serverVersion": "Microsoft SQL Azure (RTM) - 12.0.2000.8"
}
```

That is the managed identity's client id and the tenant id. No password was supplied, and on this
server none could have been — `azureADOnlyAuthentication` is on.

**API → Service Bus.** A full round trip, because connecting proves less than it looks: Service Bus
will accept a connection and *then* refuse the operation, so send and receive together are what
demonstrate both roles are in force.

```json
{ "ok": true, "elapsedMs": 2720, "sentMs": 2181, "queue": "identity-probe", "correlationMatched": true }
```

**Key Vault.** The endpoint never returns the value — only its shape, which is enough to confirm a
real value arrived:

```json
{ "ok": true, "resolved": true, "vault": "kv-idty-dev-sesxqu", "lengthChars": 43, "sha256Prefix": "2907909D96CC8CFB" }
```

It also detects the failure that would otherwise be silent: when a Key Vault reference fails to
resolve, App Service leaves the literal `@Microsoft.KeyVault(...)` string as the value, and the app
starts normally and signs requests with the reference text. Checking for that prefix turns a silent
misconfiguration into a named one.

---

## 5. The step no template can express, finally solved

Reaching the SQL *server* is an Entra token. Being allowed to read a *table* is a database-level
grant, executed inside the database:

```sql
CREATE USER [id-identity-dev-sesxqu] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [id-identity-dev-sesxqu];
ALTER ROLE db_datawriter ADD MEMBER [id-identity-dev-sesxqu];
ALTER ROLE db_ddladmin  ADD MEMBER [id-identity-dev-sesxqu];
```

There is no ARM resource for it, so a template alone produces an app that authenticates
successfully and cannot read a row — and the error, `Login failed for user
'<token-identified principal>'`, reads like a credential problem.

**Day 24 stopped here** because the documented way to run it needs `sqlcmd`, which is not installed
on this machine. Requiring a separate install for one statement is a poor dependency when the
machine already has the .NET SDK, so `tools/SqlGrant` does the same job with no new prerequisites:

```
Connecting to sql-identity-dev-sesxqu.database.windows.net/identity as the signed-in Entra principal...
  connected as : <signed-in-user>
  database     : identity

Granting [id-identity-dev-sesxqu]...
  created via  : FROM EXTERNAL PROVIDER
  roles        : db_datareader, db_datawriter, db_ddladmin

=== Verified in sys.database_principals ===
  name  : id-identity-dev-sesxqu
  type  : EXTERNAL_USER  (EXTERNAL_USER means an Entra principal)
  sid   : 0x0B01665DB6DA7E4EA2CE53F86609F561
  roles : db_ddladmin, db_datareader, db_datawriter
```

That SID is the identity's client id as little-endian bytes — `5d66010b-dab6-4e7e-…` becomes
`0B01665D-B6DA-7E4E-…`. It matters because `FROM EXTERNAL PROVIDER` asks the SQL server to resolve
the name in Entra, which needs the server's own identity plus the tenant-wide **Directory Readers**
role — a grant a subscription owner frequently cannot make, and whose failure names SQL rather than
the directory. The tool tries the readable form first and falls back to
`CREATE USER [x] WITH SID = 0x…, TYPE = E`, which needs no directory access, then reports which one
worked instead of hiding it. Here the readable form was permitted.

It also reads the result back out of `sys.database_principals` rather than trusting that the
statements succeeded. A grant that "ran without error" and produced no principal is the failure
worth catching.

---

## 6. Honest gaps

- **App Insights ingestion still uses a key.** `APPLICATIONINSIGHTS_CONNECTION_STRING` contains an
  `InstrumentationKey`. It is an ingestion identifier that grants write-only access to a telemetry
  stream and cannot read anything, which is why the classifier marks it `NOTED` rather than
  `SECRET` — but Entra authentication for ingestion exists and would remove even that. Setting
  `DisableLocalAuth: true` on the component is the change; it is not made here, so this is the one
  key-shaped value left in app settings and it is labelled rather than glossed over.
- **The SQL admin is a person, not a group.** `deploy.sh` uses the signed-in user. Production wants
  an Entra group: a group survives someone leaving the team, while a named individual becomes an
  orphaned admin the day they change roles.
- **The app registration is not in code.** An app registration is a Microsoft Graph object, so ARM
  cannot create one and `scripts/entra-app.sh` does it. This is a genuine seam in "everything is
  infrastructure as code"; Bicep Graph extensibility exists in preview and is not used here.
- **Everything is publicly reachable** behind Entra rather than behind a network boundary. SQL, the
  vault and Service Bus all have `publicNetworkAccess: Enabled`. Identity is the control, and
  defence in depth would put private endpoints in front of all three. This does not.
- **No token caching in the app beyond the credential's own.** Fine at this scale; a high-traffic
  service would want explicit control over token lifetime and refresh.
- **No CI.** Day 17's OIDC federated-credential workflow is the pattern to reuse, and it is worth
  noting that it is the same idea one level up: the pipeline authenticates to Azure with no stored
  secret either.
- **`azd`/deployment stacks are not used.** Day 24 built that; this deploys with
  `az deployment group create` so the day's subject stays identity rather than lifecycle. Wrapping
  it in a stack would layer cleanly.
