// =============================================================================================
// DEV parameters for Dispatch.
//
// `.bicepparam`, not a JSON parameter file. The difference is not cosmetic: `using` binds this
// file to main.bicep, so the compiler CHECKS it — a misspelled parameter, a missing required
// one, or a value outside an @allowed list is an error before anything is submitted to Azure.
// A JSON parameter file is untyped text that only fails at deployment time, several minutes and
// one half-created resource group later.
//
// ---------------------------------------------------------------------------------------------
// THE SHAPE OF DEV: cheap, disposable, and honest about being neither durable nor fast.
//
// Every value below is chosen to make an idle environment cost as close to nothing as Azure
// allows, and to fail loudly rather than expensively.
// =============================================================================================

using './main.bicep'

param environmentName = 'dev'
param location = 'centralindia'

// ---------------------------------------------------------------------------------------------
// SQL — serverless, so an environment nobody is using stops billing for compute.
//
// GP_S_Gen5_1 auto-pauses after an hour idle. The cost is a cold-start of several seconds on the
// first query after a pause, which is exactly the right trade for dev and exactly the wrong one
// for prod.
// ---------------------------------------------------------------------------------------------
param databaseSku = {
  name: 'GP_S_Gen5_1'
  tier: 'GeneralPurpose'
  family: 'Gen5'
  capacity: 1
}

// Seven days is the Azure minimum that still lets you recover from "I ran the wrong migration
// on Friday" on Monday morning.
param backupRetentionDays = 7

// No zone redundancy. It roughly doubles the cost to survive a datacentre failure in an
// environment whose data is disposable by definition.
param sqlZoneRedundant = false

// ---------------------------------------------------------------------------------------------
// Service Bus — Standard, which is the MINIMUM tier that supports topics at all.
//
// Basic would be cheaper and gives queues only, which cannot express the fan-out Dispatch's
// design depends on. This is the one place dev cannot economise without changing the
// architecture being tested.
// ---------------------------------------------------------------------------------------------
param serviceBusSku = 'Standard'
param serviceBusCapacity = 1

// Three deliveries then dead-letter. Deliberately LOW in dev: a poison message should reach the
// DLQ quickly so it can be looked at, rather than being retried ten times while somebody waits.
param maxDeliveryCount = 3

// One day. Long enough to debug over a lunch break, short enough that a forgotten test message
// does not sit in the topic for a fortnight.
param messageTimeToLive = 'P1D'

// ---------------------------------------------------------------------------------------------
// API — the smallest supported container, allowed to scale to zero.
//
// minReplicas: 0 is the single largest cost saving in this file. An idle dev environment runs no
// containers and bills no compute. The cost is a cold start on the first request, which is
// acceptable everywhere except production.
// ---------------------------------------------------------------------------------------------
param containerResources = {
  cpu: json('0.25')
  memory: '0.5Gi'
}

param minReplicas = 0
param maxReplicas = 2

// ---------------------------------------------------------------------------------------------
// Observability — 30 days is the Log Analytics floor, and a hard 1 GB/day cap.
//
// The cap matters more than the retention. A logging bug in dev can emit gigabytes in an hour,
// and the cap turns that into "ingestion stopped" rather than a surprise invoice.
// ---------------------------------------------------------------------------------------------
param logRetentionDays = 30
param logDailyQuotaGb = 1

// ---------------------------------------------------------------------------------------------
// SQL administration — an Entra GROUP object id, never a person and never a password.
//
// A group survives someone leaving the team; a named individual becomes an orphaned admin the
// day they change roles. This is a public identifier, not a secret.
//
// Replace with your own group's object id:
//   az ad group create --display-name "sql-dispatch-admins" --mail-nickname "sql-dispatch-admins"
//   az ad group show --group "sql-dispatch-admins" --query id -o tsv
// ---------------------------------------------------------------------------------------------
// Read from the environment, not committed.
//
// `readEnvironmentVariable` is a .bicepparam feature a JSON parameter file cannot express,
// and it is the right tool here: an object id is a public identifier, but it is tied to a
// specific directory, so a committed one is wrong for everybody else who clones this. CI
// supplies it as a pipeline variable; the fallback keeps `bicep build-params` working
// offline for anyone who just wants to type-check the file.
param sqlAdminObjectId = readEnvironmentVariable('SQL_ADMIN_OBJECT_ID', '00000000-0000-0000-0000-000000000000')
param sqlAdminLogin = 'sql-dispatch-admins'

// A public image so a first deployment succeeds before any registry or built image exists.
// Swapped for the real one by the deploy pipeline.
param containerImage = 'mcr.microsoft.com/k8se/quickstart:latest'

// ---------------------------------------------------------------------------------------------
// Reuse the environment that already exists in this region.
//
// Azure allows one Container Apps environment per region per subscription, and one is already
// deployed here. Creating a second is not a quota to raise, it is a hard limit — so dev shares.
// ---------------------------------------------------------------------------------------------
param existingManagedEnvironmentId = readEnvironmentVariable('CONTAINER_APP_ENV_ID', '')
