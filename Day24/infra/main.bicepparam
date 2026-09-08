// =============================================================================================
// The ONE parameter file, for both environments.
//
// Day 23 had two: main.dev.bicepparam and main.prod.bicepparam, each passed explicitly on the
// command line. azd does not work that way — it binds one parameter file to one template and
// distinguishes environments through the azd environment itself. So the choice moves inside.
//
// ---------------------------------------------------------------------------------------------
// WHY THIS IS NOT THE BRANCHING DAY 23 REFUSED TO DO
//
// Day 23's rule was that nothing may branch on `environmentName` to decide a SIZE. That rule is
// intact. There is exactly one conditional in this file and it selects a whole PROFILE — a
// committed, reviewable document naming every value at once. It never decides an individual SKU,
// replica count or retention period.
//
// The distinction matters because of what each version is like to review. Sixteen inline
// ternaries have to be read sixteen times to answer "what is prod?", and any one of them can be
// wrong on its own. Two JSON files answer that question by being diffed against each other, and
// a value can only be wrong by being wrong in the file that describes that environment.
//
//   diff infra/profiles/dev.json infra/profiles/prod.json
//
// is the whole dev-versus-prod story, and it is the reason the profiles are separate files
// rather than two keys in one.
//
// ---------------------------------------------------------------------------------------------
// WHY loadJsonContent AND NOT A PARAMETER
//
// `loadJsonContent` is resolved by the COMPILER, so the profile is embedded in the template
// before anything reaches Azure. A missing file or malformed JSON is a build error on this
// machine. The alternative — passing the sizing in as an object parameter — moves that failure
// to deployment time, where a typo costs a round trip and a half-created resource group.
//
// The cost of the compiler resolving it is that the path must be a literal, which is exactly why
// the selection below is a ternary and not `loadJsonContent('profiles/${name}.json')`. That
// would be tidier and does not compile.
// =============================================================================================

using './main.bicep'

// ---------------------------------------------------------------------------------------------
// azd writes AZURE_ENV_NAME into the environment before it invokes Bicep, so the azd environment
// you selected IS the profile you get. `azd env select prod` is the entire promotion mechanism.
//
// The default is 'dev', which makes the safe environment the one you get when the variable is
// missing. Defaulting to 'prod' would mean a broken shell deploys production.
// ---------------------------------------------------------------------------------------------
var envName = readEnvironmentVariable('AZURE_ENV_NAME', 'dev')

var profile = envName == 'prod'
  ? loadJsonContent('profiles/prod.json')
  : loadJsonContent('profiles/dev.json')

param environmentName = envName
param location = readEnvironmentVariable('AZURE_LOCATION', 'centralindia')

// ---- sizing, straight from the profile ------------------------------------------------------

param databaseSku = profile.databaseSku
param backupRetentionDays = profile.backupRetentionDays
param sqlZoneRedundant = profile.sqlZoneRedundant

param serviceBusSku = profile.serviceBusSku
param serviceBusCapacity = profile.serviceBusCapacity
param maxDeliveryCount = profile.maxDeliveryCount
param messageTimeToLive = profile.messageTimeToLive

// `json()` rather than a number in the JSON file. Bicep has no float literal, and a bare 0.25
// in a loaded document is a float — so the profile stores CPU as a string and it is converted
// here, which keeps the profiles loadable and the conversion in one visible place.
param containerResources = {
  cpu: json(profile.containerCpu)
  memory: profile.containerMemory
}

param minReplicas = profile.minReplicas
param maxReplicas = profile.maxReplicas

param logRetentionDays = profile.logRetentionDays
param logDailyQuotaGb = profile.logDailyQuotaGb

param sqlAdminLogin = profile.sqlAdminLogin

// ---------------------------------------------------------------------------------------------
// The two values that are NOT in a profile, because they are not sizing — they are identifiers
// specific to one directory and one subscription.
//
// Neither is a secret: an Entra object id and a resource id are both public. Committing them
// would still be wrong, because they are meaningless to anyone else who clones this repo. azd
// stores them per-environment in .azure/<env>/.env, set once by scripts/env-setup.sh.
//
// The fallbacks keep `bicep build-params` working offline for anyone who only wants to
// type-check the file.
// ---------------------------------------------------------------------------------------------
param sqlAdminObjectId = readEnvironmentVariable('SQL_ADMIN_OBJECT_ID', '00000000-0000-0000-0000-000000000000')
param existingManagedEnvironmentId = readEnvironmentVariable('CONTAINER_APP_ENV_ID', '')

// A public image, so a first deployment succeeds before any registry or built image exists.
// Pinned by digest in a real pipeline, never `:latest` — a tag can be moved, so `:latest` means
// "whatever was pushed most recently", which is not a deployable description of anything.
param containerImage = readEnvironmentVariable('CONTAINER_IMAGE', 'mcr.microsoft.com/k8se/quickstart:latest')
