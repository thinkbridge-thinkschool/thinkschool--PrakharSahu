// =============================================================================================
// PROD parameters for Dispatch.
//
// Same template, same modules, same resource shapes. Every difference from dev is in this file,
// which is the property the whole exercise is testing: if prod needed a different main.bicep,
// then dev was never a rehearsal for anything.
//
// ---------------------------------------------------------------------------------------------
// THE SHAPE OF PROD: durable, always-warm, and expensive in the places where being cheap costs
// more than the saving.
// =============================================================================================

using './main.bicep'

param environmentName = 'prod'
param location = 'centralindia'

// ---------------------------------------------------------------------------------------------
// SAME region as dev, and it should not be. This is a subscription limitation, not a design.
//
// Prod belongs in its own region: sharing one means a regional outage takes dev and prod
// together, so the environment meant to rehearse a recovery is gone at exactly the moment it is
// needed.
//
// It is here anyway because this subscription refuses anything else. The first attempt put prod
// in koreacentral and what-if returned:
//
//   MaxNumberOfGlobalEnvironmentsInSubExceeded - The subscription cannot have more than 1
//   Container App Environments.
//
// Not one per region — ONE, across the whole subscription. So prod cannot have an environment of
// its own in any region, and the honest resolution is to reuse the same one and say so. On a
// subscription without that cap, this line becomes a different region and the template needs no
// other change.
// ---------------------------------------------------------------------------------------------

// ---------------------------------------------------------------------------------------------
// SQL — PROVISIONED, not serverless.
//
// This is the most consequential line in the file. Serverless auto-pause is what makes dev cheap,
// and it is exactly what must not happen in production: the first request after a pause waits
// several seconds for the database to resume, and it arrives at whatever hour traffic is
// lightest — which is to say, at the worst possible moment for the one user who is awake.
//
// S1 is modest, and deliberately so: it is a starting point sized from nothing, and the honest
// position is that the right SKU is unknowable until real traffic exists. What matters is that
// changing it is a one-line edit to this file rather than a redesign.
// ---------------------------------------------------------------------------------------------
param databaseSku = {
  name: 'S1'
  tier: 'Standard'
  capacity: 20
}

// The Azure maximum for short-term retention. Point-in-time restore across five weeks covers
// "the corruption started before anyone noticed", which is the case that actually needs backups.
param backupRetentionDays = 35

// Zone redundancy ON. This is the line that survives a datacentre failure, and the reason the
// backup storage in sql.bicep also becomes zone-redundant — a zone-redundant database with
// locally-redundant backups protects the running system and not its recovery path.
param sqlZoneRedundant = true

// ---------------------------------------------------------------------------------------------
// Service Bus — PREMIUM.
//
// Not for throughput. Premium buys three things Standard cannot: dedicated resources so a noisy
// neighbour cannot affect latency, zone redundancy, and a predictable cost instead of per-
// operation billing that scales with a bug.
//
// Standard would work, and would be defensible for a low-volume service. The reason it is not
// chosen here is the first of those three: on Standard, tail latency is somebody else's traffic.
// ---------------------------------------------------------------------------------------------
param serviceBusSku = 'Premium'
param serviceBusCapacity = 1

// Ten deliveries before dead-lettering, against dev's three. A production consumer failing is
// far more likely to be a transient downstream outage than a poison message, and burning the
// retry budget in thirty seconds turns a recoverable blip into a DLQ a human has to drain.
param maxDeliveryCount = 10

// Fourteen days. Long enough that a subscription broken over a public holiday still has its
// messages when somebody comes back to it.
param messageTimeToLive = 'P14D'

// ---------------------------------------------------------------------------------------------
// API — four times the CPU, and NEVER fewer than two replicas.
//
// minReplicas: 2, not 1. One replica means every deployment, every node drain and every crash is
// a total outage for as long as the replacement takes to start. Two is the smallest number that
// makes a rolling restart invisible, and it is the difference between "we deploy at 2am" and
// "we deploy whenever".
// ---------------------------------------------------------------------------------------------
param containerResources = {
  cpu: json('1.0')
  memory: '2Gi'
}

param minReplicas = 2
param maxReplicas = 10

// ---------------------------------------------------------------------------------------------
// Observability — 90 days, and a much higher cap.
//
// Thirty days is fine for "why did the build fail". Ninety is the floor for "this started
// degrading gradually and nobody noticed for a month", which is the incident telemetry is
// actually for.
//
// The cap is raised rather than removed. Uncapped ingestion has no upper bound on cost, and a
// logging bug in production emits far more than one in dev.
// ---------------------------------------------------------------------------------------------
param logRetentionDays = 90
param logDailyQuotaGb = 10

// ---------------------------------------------------------------------------------------------
// SQL administration — the same shape as dev, and in practice a DIFFERENT group.
//
// Sharing one admin group across dev and prod means anyone who can break dev can break
// production, which quietly undoes the reason for having two environments.
// ---------------------------------------------------------------------------------------------
// Read from the environment, not committed.
//
// `readEnvironmentVariable` is a .bicepparam feature a JSON parameter file cannot express,
// and it is the right tool here: an object id is a public identifier, but it is tied to a
// specific directory, so a committed one is wrong for everybody else who clones this. CI
// supplies it as a pipeline variable; the fallback keeps `bicep build-params` working
// offline for anyone who just wants to type-check the file.
param sqlAdminObjectId = readEnvironmentVariable('SQL_ADMIN_OBJECT_ID', '00000000-0000-0000-0000-000000000000')
param sqlAdminLogin = 'sql-dispatch-admins-prod'

// Pinned by digest in a real pipeline, never `:latest` — a tag can be moved, so `:latest` means
// "whatever was pushed most recently", which is not a deployable description of anything.
param containerImage = 'mcr.microsoft.com/k8se/quickstart:latest'

// Forced by the one-environment-per-subscription cap described above. On a normal subscription
// this would be empty, and prod would create its own.
param existingManagedEnvironmentId = readEnvironmentVariable('CONTAINER_APP_ENV_ID', '')
