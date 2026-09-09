# Day 25 — every file, and why

A walkthrough of what was built, the four things that broke on the way, and the one claim from an
earlier day that this finally made good on.

---

## `infra/main.bicep` — the ordering is the design

The composition root, and the only interesting thing about it is the sequence:

```bicep
module identity      // 1. the principal
module keyVault      // 2. the data plane
module sql
module serviceBus
module rbac          // 3. every grant, dependsOn: [keyVault, serviceBus]
module app           // 4. the app, dependsOn: [rbac]
```

That last `dependsOn` is the whole argument for using a **user-assigned** identity instead of the
system-assigned one Days 23 and 24 used.

A system-assigned identity does not exist until its host resource is created, so every role
assignment must come *after* the app. Key Vault references resolve at **startup** — during that same
deployment, before the grant lands. The app boots, asks the vault for a secret, is refused, and
caches a failed reference. It then keeps running with the literal `@Microsoft.KeyVault(...)` string
as the setting value.

A user-assigned identity is an independent resource, so it can be created and fully granted before
the app exists. By the time the app starts, its identity already has every permission it will ever
need.

Day 24's bug was the same shape from the other direction — an `existing` lookup that created no
dependency, so ARM ran two modules in parallel and one failed on a resource the other had not
finished creating. Both are cases of a dependency that is real and invisible to ARM unless stated.

## `infra/modules/identity.bicep` — three ids, three jobs

Small file, and it exists mostly to name the distinction that causes the most confusion:

```bicep
output resourceId  string = identity.id                          // ATTACH the identity
output principalId string = identity.properties.principalId      // GRANT to it (Entra object id)
output clientId    string = identity.properties.clientId         // what CODE passes
```

`clientId` is the one application code needs, because a resource can carry several user-assigned
identities and the token endpoint will not guess which one is meant. `principalId` is what a role
assignment targets. Swapping them produces errors that name neither.

## `infra/modules/sql.bicep` — the line that matters

```bicep
administrators: {
  administratorType: 'ActiveDirectory'
  azureADOnlyAuthentication: true
}
```

Without that flag a server can have *both* an Entra admin and a SQL login, and the SQL login is the
one that ends up in a connection string. "We use managed identity" is then true of the code written
last sprint and false of the batch job from two years ago — and the password is still valid, still
un-rotated, and still wherever it was pasted.

With it, password authentication is refused at the server. `administratorLoginPassword` is not
merely omitted from the template, it is unusable.

The trade is worth stating: every client must now be able to get an Entra token, and a legacy tool
that only speaks SQL logins cannot connect at all. That is the point, and it is also why this is a
decision to make deliberately rather than a checkbox.

The firewall rule is named `AllowAzureServices` rather than Azure's conventional
`AllowAllWindowsAzureIps`, for the reason Day 24 recorded: azd's linter flags the substring
"Windows" as a reserved word and claims the deployment will fail. It does not, but a warning that
cries wolf on every run gets skimmed — and Day 24 proved that is exactly when a real one is missed.

## `infra/modules/servicebus.bicep` — making the keys worthless

```bicep
disableLocalAuth: true
```

Every namespace is created with a `RootManageSharedAccessKey` whether you want one or not. Not
using it is a *convention*, and conventions break when somebody needs a queue working before a demo
and finds `az servicebus namespace authorization-rule keys list` in a search result.

This makes the namespace reject those keys. The rule is still listed and the key can still be read
by anyone with management rights; a client presenting it is refused. The failure mode changes from
"somebody quietly uses a shared secret and nobody notices" to "it does not work, immediately, for
everyone" — and a control that fails loudly is the only kind that survives a deadline.

## `infra/modules/keyvault.bicep` — and the secret that is deliberately not in it

Two settings do the work. `enableRbacAuthorization: true` puts vault access in the same place as
every other permission in the subscription; access policies are per-vault, invisible to
`az role assignment list`, and cannot be reasoned about from outside the vault, so an audit using
them can never be complete. `enablePurgeProtection: true` means a deleted secret cannot be
permanently erased before its retention window elapses — the setting people disable because it
makes teardown awkward, and the one that makes a mistaken delete recoverable.

There is **no** `Microsoft.KeyVault/vaults/secrets` resource in the file, and that absence is the
design. Creating the secret in Bicep would mean its value arrives as a template parameter, and a
parameter is recorded in the deployment history whether or not it is `@secure()` — secure values are
*redacted in the portal*, not absent from the request that carried them. It would also mean the
value passed through a shell variable, a script argument, and potentially a CI log.

So the template creates the container and `deploy.sh` fills it with `az keyvault secret set`, which
writes to the data plane and never touches ARM. The value exists in exactly two places: the vault,
and the memory of the process that generated it.

A note on what Key Vault is *not* for here: it is not the answer to "where do we put the SQL
password". The answer to that is to stop having one. A vault holding a SQL password is a smaller
version of the same problem — the credential still exists, still never rotates on its own, and now
there is one more hop to misconfigure.

## `infra/modules/rbac.bicep` — narrow roles, and why it is a separate module

A role assignment's name and scope must both be computable before the deployment starts. Build
either from a value that only exists once another resource has been created and Bicep refuses:

```
BCP120: ... requires a value that can be calculated at the start of the deployment.
```

Module *parameters* are start-known by definition, so a module boundary satisfies the compiler
without weakening anything. Day 23 found this the hard way.

The roles are the narrow ones — `Key Vault Secrets User` not `Key Vault Administrator`, Sender and
Receiver separately rather than `Azure Service Bus Data Owner`. `principalType: 'ServicePrincipal'`
is set explicitly because without it ARM looks the principal up in Entra, and a freshly created
identity may not have replicated yet — producing `PrincipalNotFound` on a principal that plainly
exists.

## `infra/modules/app.bicep` — where the claim is either true or false

Every other module removed a *reason* to hold a secret. This is where the absence has to show,
because these app settings are exactly what a reviewer dumps.

Two lines are easy to miss and both are required:

```bicep
keyVaultReferenceIdentity: managedIdentityId
```

Which identity resolves Key Vault references. The default is the **system-assigned** identity, and
this site does not have one — omit this and every reference fails with an access error naming a
principal that does not exist.

```bicep
connectionStrings: []
```

The slot purpose-built for connection strings, left empty rather than omitted. An empty array is a
statement; an absent property is an oversight that looks identical from the outside.

The Easy Auth block is configured to **validate** tokens rather than issue them —
`unauthenticatedClientAction: 'Return401'`, no `clientSecretSettingName`. Issuing tokens means an
authorization-code exchange, and that exchange is the only step that requires the app to prove it is
itself, i.e. the only reason a client secret would exist. For an API, 401 is also simply correct: a
JSON client handles it and cannot meaningfully follow a login redirect.

`allowedAudiences` is not decoration. A token minted for a different application is signed by the
same tenant and is still not for this API; without the audience check it would be accepted, which is
the classic confused-deputy hole.

## `src/IdentityApi` — because configuration that looks right proves nothing

Five endpoints, each exercising one path and reporting what actually happened. `/probe/sql` is the
strongest: `SUSER_SNAME()` returns the principal the *server* believes is connected, so a managed
identity name coming back means the database authenticated an Entra token.

One credential object, registered as a singleton:

```csharp
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ManagedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"]
});
```

Singleton because `DefaultAzureCredential` caches tokens internally, and a per-request instance
throws that cache away — turning a 2 ms operation into a network round trip and risking throttling.

`/probe/keyvault` never returns the secret, only its length and a truncated SHA-256. More usefully,
it detects the failure that is otherwise silent: a failed Key Vault reference leaves the literal
`@Microsoft.KeyVault(...)` string as the value, and the app runs normally while signing requests
with the reference text. Checking for that prefix turns a silent misconfiguration into a named one.

`/probe/servicebus` does a send **and** a receive, because Service Bus will accept a connection and
then refuse the operation — connecting alone proves less than it appears to.

The index at `/` is deliberately *not* a roll-up that reports all three probes. Doing it properly
means re-entering the pipeline in-process; doing it improperly means an endpoint that looks like it
ran the probes and did not. A summary that reports success without executing anything is the exact
failure mode this whole exercise is about.

## `tools/SqlGrant` — the step that stopped Day 24

Reaching the SQL server is a token. Being allowed to read a table is a database-level grant, and
there is no ARM resource for it. Day 24 emitted the statement, printed it, and never ran it, because
the documented way needs `sqlcmd` and `sqlcmd` is not installed here. That left an app that
authenticated successfully and could not read a row.

This does the same job with the .NET SDK that is already on the machine, and adds two things a
pasted statement does not:

**A fallback that does not need directory access.** `FROM EXTERNAL PROVIDER` asks the SQL server to
resolve the name in Entra, which requires the server's own identity plus the tenant-wide
**Directory Readers** role — a grant a subscription owner frequently cannot make, and whose failure
names SQL rather than the directory. A managed identity's SQL SID is simply its client id as
little-endian bytes, so the user can be created from a value already in hand:

```sql
CREATE USER [x] WITH SID = 0x0B01665DB6DA7E4EA2CE53F86609F561, TYPE = E;
```

The tool tries the readable form first, falls back, and reports which one worked rather than hiding
it. On this tenant the readable form was permitted.

**A read-back.** It queries `sys.database_principals` afterwards rather than trusting that the
statements succeeded. A grant that ran without error and produced no principal is the failure worth
catching.

## `scripts/entra-app.sh` — the genuine seam in "everything is code"

An app registration is a Microsoft Graph object, not an Azure resource. It lives in the Entra
directory beside the subscription rather than inside it, and ARM cannot create, read or delete one.
Bicep Graph extensibility exists in preview and is not used here.

So this is a real gap in infrastructure-as-code, and pretending otherwise would be the dishonest
part. What the script can do is be idempotent and emit the one value the template needs — a client
id, which is public.

What it deliberately does **not** do is run `az ad app credential reset`. No client secret and no
certificate is created, because the API validates tokens rather than issuing them. The proof script
checks this from the other side: `passwordCredentials: 0`, `keyCredentials: 0`.

## `scripts/prove-no-secrets.sh` — the deliverable

Twelve assertions in two halves. The first half is about **absence**: every app setting classified,
the connection-strings slot empty, no `secureString` in deployment history. The classifier treats
anything it cannot categorise as a failure, because an unclassified value is an unreviewed one and
"we found nothing" only means something if everything was examined.

The second half is about **capability**, and it is the stronger half: Entra-only SQL, local auth
disabled, RBAC on the vault, no credentials on the app registration, no broad roles on the identity.
Absence of a secret in a config blade is weak evidence. Inability to *use* one is strong.

It also declines to grade on a curve. The App Insights connection string contains an
`InstrumentationKey`, and rather than excluding it from the scan the classifier marks it `NOTED`
with a pointer to the gap it represents. A proof that quietly skips its own inconvenient case is not
a proof.

---

## Four things that broke

### 1. `InvariantGlobalization` crashes Microsoft.Data.SqlClient

The reflexive setting for a small console tool — it trims the ICU dependency. The driver refuses to
open a connection under it:

```
System.NotSupportedException: Globalization Invariant Mode is not supported.
```

It surfaces at `Open()`, not at build time, and it is a `NotSupportedException` rather than a
`SqlException` — so it escaped the `catch` around the connection attempt and came out as an
unhandled crash. Removed from both projects, with the reason recorded in the `.csproj` so it does
not get re-added.

### 2. The SQL firewall refused the grant, and the error blamed the login

```
Cannot open server '<server>' requested by the login. Client with IP address
'<ip>' is not allowed to access the server.
```

That names a *login* problem and is a *network* problem. The template admits Azure services only,
which is correct — the App Service is the only thing that should reach this server in normal
operation — but the grant runs from a workstation.

`grant-sql.sh` now opens a narrow rule for one address, named so its purpose is obvious in the
portal, and removes it with a `trap` on exit so it goes away even if the grant fails or the script
is interrupted. A permanent "allow my laptop" rule is how a server ends up reachable from an address
nobody recognises two months later.

`SqlGrant` also now distinguishes the three causes that all read like authentication failures — a
firewall refusal, a missing admin, and a serverless database still resuming — and says which one it
is.

### 3. `/tmp` means two different things

The proof script writes JSON with bash and reads it with Python. Under Git Bash on Windows those
disagree: bash resolves `/tmp` inside the MSYS root, and the Windows Python interpreter resolves it
to a `tmp` directory on the C: drive that does not exist. So the file bash wrote was genuinely
there, and `json.load(open('/tmp/x.json'))` raised `FileNotFoundError`.

Section 1 crashed visibly. **Section 3 caught the same exception, printed `indeterminate`, and
quietly fell through to a weaker check** — while looking exactly like it had run. Fixed with a
relative temp directory, which both interpret identically because both start from the same working
directory.

### 4. The Key Vault resolution API is a GET, not a POST

Once section 3 could read its own file, it turned out to be asking the wrong way. The POST form of
`/config/configreferences/appsettings` returns an empty body; the GET form returns the platform's
full verdict:

```
  ThirdParty__WebhookSigningKey
    status      : Resolved
    detail      : Reference has been successfully resolved.
    resolved by : UserAssigned identity
    vault/secret: kv-idty-dev-sesxqu / payments-webhook-signing-key
```

That `resolved by: UserAssigned identity` line is worth more than the status: it confirms *which*
identity read the vault, which is the part `keyVaultReferenceIdentity` exists to control.

Both of these are the same failure as a green what-if that skipped a third of the plan, or a green
test suite measuring the wrong strategy. A check that cannot fail is not a check, and the way it
hides is by having a fallback that looks like success.
