# Day 24 — every file, and why

A walkthrough of what was added, what was carried over from Day 23 unchanged, and the three
things that had to change because the template met a real deployment.

---

## New files

### `azure.yaml` — the azd manifest

The whole azd configuration, and the file that replaces Day 23's `whatif.sh` and `deploy.sh`.
What it no longer has to express is the interesting part: no resource group name, no
`az group create`, no `--parameters main.dev.bicepparam`, no timestamped deployment name. azd
derives all of that from the selected environment, which is what makes `azd provision` identical
for dev and prod.

The `deploymentStacks` block is the day's subject:

```yaml
deploymentStacks:
  actionOnUnmanage:
    resources: delete
    resourceGroups: delete
    managementGroups: detach
  denySettings:
    mode: denyDelete
    applyToChildScopes: false
    excludedPrincipals: []
    excludedActions: []
```

`resources: delete` hands lifecycle ownership to the stack — drop a resource from the template and
the next provision removes it from Azure rather than orphaning it. `resourceGroups: delete` is why
`main.bicep` moved to subscription scope; it is meaningless at resource-group scope.

`denyDelete` rather than `denyWriteAndDelete` is a deliberate narrowing. Write-deny would block the
platform's own legitimate mutations — a Container Apps revision, a scale event, a certificate
rotation — and the app would degrade in ways that look like application bugs. Delete is the
irreversible operation, so delete is what gets denied.

`excludedPrincipals: []` is the correct configuration, not an unfinished one. A deny assignment
does not lock the owner out of teardown, because `azd down` deletes nothing directly — it calls the
stack API, which removes the deny assignment first and then the resources. An exclusion is only
needed for a principal that must bypass the stack entirely, and adding one "just in case" is a hole
in the only protection this block provides.

### `infra/main.bicep` — subscription scope

The only genuinely new Bicep. It exists to make the resource group a **managed resource**:

```bicep
targetScope = 'subscription'

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-dispatch-${environmentName}'
  location: location
  tags: { /* ... */ 'azd-env-name': environmentName }
}

module resources 'resources.bicep' = {
  name: 'dispatch-resources'
  scope: resourceGroup
  params: { /* every parameter, passed through */ }
}
```

Day 23's scripts ran `az group create` before every plan. That group was owned by nothing: no
template described it, no teardown removed it, and it outlived the thing it was created for — two
of them are still in this subscription's history for exactly that reason. Declaring it here puts it
inside the stack's inventory, so it is created by the same command that creates everything else and
removed by the same command that removes everything else.

Outputs are renamed to `SCREAMING_SNAKE_CASE` because azd writes each one into the environment's
`.env` file, where the next command and any hook reads it by name.

### `infra/main.bicepparam` — one parameter file, both environments

Day 23 passed `main.dev.bicepparam` or `main.prod.bicepparam` explicitly. azd binds one parameter
file to one template, so the selection moves inside:

```bicep
var envName = readEnvironmentVariable('AZURE_ENV_NAME', 'dev')

var profile = envName == 'prod'
  ? loadJsonContent('profiles/prod.json')
  : loadJsonContent('profiles/dev.json')
```

The default is `dev`, so a broken shell deploys the safe environment rather than production.

This does not break Day 23's rule that nothing may branch on `environmentName` to decide a **size**.
There is one conditional and it selects a whole profile; it never decides an individual SKU, replica
count or retention period.

`loadJsonContent` is resolved by the compiler, so the profile is embedded in the template before
anything reaches Azure and a malformed file is a build error locally. The price is that the path
must be a literal — which is why this is a ternary and not
`loadJsonContent('profiles/${envName}.json')`. That version is tidier and does not compile.

One conversion is worth noting:

```bicep
param containerResources = {
  cpu: json(profile.containerCpu)      // "0.25" -> 0.25
  memory: profile.containerMemory
}
```

Bicep has no float literal, so the profiles store CPU as a string and it is converted in one visible
place rather than risking a float in a loaded JSON document.

### `infra/profiles/dev.json`, `infra/profiles/prod.json`

Day 23's two `.bicepparam` files, converted. The reasoning that was in Bicep comments is preserved
as `_`-prefixed JSON arrays, so the *why* travels with the values.

There is a real trade here and it is named in `dev.json`: a `.bicepparam` is type-checked against
`main.bicep`, so a misspelled parameter is a compile error. These files are not type-checked. The
safety net is that `main.bicepparam` references every key explicitly, so a typo still fails at
compile time — and that only holds because nothing reads these files dynamically.

What is gained is that `diff infra/profiles/dev.json infra/profiles/prod.json` is the entire
dev-versus-prod story in one command. `scripts/promote.sh` prints exactly that before it deploys.

### `scripts/env-setup.sh`

Creates the azd environment and resolves the two values no profile can carry: the SQL admin group's
object id and the shared Container Apps environment's resource id. Neither is a secret — both are
public identifiers — but both are specific to one directory and one subscription, so committing them
would be wrong for anyone else who clones this.

Idempotent by design. `azd env new` fails if the environment exists, which is the wrong behaviour for
a setup script somebody runs twice, so it checks and selects instead.

It creates the Entra admin group if missing and **adds the signed-in user to it**. Entra-only auth
means there is no password to fall back on, so an admin group with no members is an unreachable
database. If the directory refuses group creation it falls back to the signed-in user and says
plainly that this is a real compromise, rather than quietly producing a template with a placeholder
admin.

### `scripts/promote.sh`

Promotion is `azd env select prod && azd provision`. The script adds the part worth having: a gate.

```bash
FROM_STATE="$(az stack sub show --name "azd-stack-dev" --query provisioningState -o tsv)"
[ "$FROM_STATE" = "succeeded" ] || die "Refusing to promote: dev is '${FROM_STATE}'."
```

Checked against Azure, not against a local file that could be stale. Promoting from a dev
environment that never deployed cleanly promotes nothing — it just runs an unproven template against
expensive resources for the first time.

### `scripts/drift-proof.sh`

Five assertions covering the stack's inventory, an out-of-band delete being refused, and a write
drift being reverted by the next provision. It introduces exactly one change to a live resource — a
tag — and reverts it by re-provisioning, which is the property being demonstrated.

It redacts before it prints. An ARM authorization error echoes back the signed-in principal's UPN
and object id, and this output is committed:

```bash
redact() {
  sed -E -e 's/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+/<user>/g' \
         -e 's/[0-9a-fA-F]{8}-...-[0-9a-fA-F]{12}/<guid>/g'
}
```

### `scripts/postprovision.sh`

The T-SQL grant no template can express — `CREATE USER [app] FROM EXTERNAL PROVIDER` runs inside the
database and has no ARM resource. Day 23 printed it for a human to run; this was meant to run it.

It checks for `sqlcmd` rather than assuming it, and exits `0` when it is missing: the provision
**succeeded**, and failing the command here would report a successful deployment as a failed one.

It does not currently achieve its purpose — see the honest note under *Corrections* below.

### `.gitignore`

Ignores `.azure/`, which holds the per-environment identifiers and every template output azd writes
back. Not secrets, but specific to one subscription and meaningless to anyone else.

---

## Carried over from Day 23

`infra/modules/observability.bicep` and `infra/modules/rbac.bicep` are byte-for-byte unchanged.
`infra/resources.bicep` is Day 23's `main.bicep` with three edits and no design change:

1. It is no longer the entry point — `main.bicep` calls it with the resource group as its scope.
2. Resources carry an `azd-env-name` tag so azd can find them again; `azd down` and
   `azd env refresh` both locate resources by it.
3. `sqlConnectionString` is exposed as an output, because azd writes outputs into `.env` and that is
   where a hook reads it from.

Everything that made Day 23 worth reviewing survives: the naming rules that keep the graph resolvable
at plan time, the module boundaries, and the property that there are no secrets because none exist
to store.

---

## Corrections — three things deploying proved wrong

Day 23 planned cleanly twice and was wrong three times. All three are outside what a plan can
observe, which is the lesson rather than a complaint about what-if.

### 1. `resources.bicep` — a missing `dependsOn`

The first dev provision failed with `ResourceNotFound` on the App Insights component the previous
line had just created.

`api.bicep` reaches the workspace and App Insights with `existing` lookups by name rather than
consuming module outputs. Day 23 chose that deliberately — consuming a module output makes the
downstream graph unresolvable at plan time and what-if reports `NestedDeploymentShortCircuited`. It
is what took Day 23 from *12-to-create-with-2-skipped* to *13-with-0-skipped*.

What it also removed, invisibly, was the dependency. ARM infers ordering from data flow; an
`existing` reference is not data flow, so ARM ran both modules in parallel.

```bicep
module api 'modules/api.bicep' = {
  // ...
  dependsOn: [
    observability
  ]
}
```

This states the dependency that genuinely exists without reintroducing the data flow that would
break the plan. Plan-time resolvability and correct ordering are both available; they just have to
be asked for separately.

### 2. `profiles/prod.json` — a SKU that could not do what was asked

Day 23 paired `S1` with `sqlZoneRedundant: true` and what-if accepted it. That cannot deploy —
zone redundancy is unsupported on the DTU-based Standard tier — because what-if validates template
shape, not whether a SKU supports the features requested alongside it. Changed to `GP_Gen5_2`.

Prod then failed anyway with `ProvisioningDisabled`, a message naming neither the region nor the
SKU. Both were checked rather than guessed:

```
az account list-locations   ->  centralindia reports availability zones 1, 2, 3
az sql db list-editions     ->  GP_Gen5_2 reports zoneRedundant=True, status=Default
```

Both support it; the Azure for Students subscription does not. `sqlZoneRedundant` is now `false`
with a long note recording that the template is correct and the subscription is the constraint —
and stating what is actually lost, which is that the recovery path now shares a failure domain with
the database it is meant to recover.

### 3. `modules/sql.bicep` — a firewall rule name that trips azd's linter

azd warned on every deployment:

```
(!) Warning: Resource "sql-.../AllowAllWindowsAzureIps" contains the reserved word "WINDOWS"
    Azure does not allow reserved words in resource names. The deployment will fail.
```

The warning is wrong — the rule deployed on the first run, while a genuine bug in the same template
went unmentioned. The name is arbitrary metadata; only the `0.0.0.0`–`0.0.0.0` pair carries meaning.
Renamed to `AllowAzureServices`, because a warning that cries wolf on every deployment is worse than
no warning: the next one gets skimmed too.

This produced an unplanned fifth proof. The rename put the old rule outside the stack's inventory,
and `actionOnUnmanage: delete` removed it without anyone asking:

```
before:  AllowAllWindowsAzureIps
after :  AllowAzureServices
```

Under a plain deployment the old rule would still be there.

---

## One claim retracted

`azure.yaml` originally said the postprovision hook "runs on every provision of every environment,
and cannot be forgotten because nobody has to remember it." That is not what happens.

With `alpha.deployment.stacks` enabled, two clean `azd provision` runs against dev were captured in
full: both finished with `SUCCESS` and neither produced a line of hook output, while the same hook
runs correctly on demand:

```
azd provision --no-prompt      -> 0 lines of hook output
azd hooks run postprovision    -> runs, reads the outputs, prints the pending GRANT
```

So the automation the hook was written to provide is not in force, and the grant is back to being a
step someone has to remember — the exact failure it was meant to remove. The hook is kept because it
is correct and the workaround is one line, but the comment now records the measurement rather than
the intention.

The same section originally claimed that deletes travelling through the stack are "always"
permitted. Tearing prod down found the exception: `azd down --purge` issues a **direct** delete of
soft-deletable resources before the stack is unmanaged, and the deny assignment blocks it with a
403. `azd down --force` works. The cost is that the Log Analytics workspace is left soft-deleted
rather than purged — it bills nothing in that state and ages out on its own.
