// =============================================================================================
// Parameters for the Day 26 telemetry deployment.
//
// One value is read from the environment: the object id of whoever runs the application locally.
// It is a public identifier, not a secret, but it is specific to one directory — a committed one
// would be wrong for anybody else who clones this. scripts/deploy.sh resolves it from the
// signed-in user.
// =============================================================================================

using './main.bicep'

param environmentName = 'dev'
param location = readEnvironmentVariable('LOCATION', 'centralindia')

param developerObjectId = readEnvironmentVariable('DEVELOPER_OBJECT_ID', '00000000-0000-0000-0000-000000000000')

// 30 days is the Log Analytics floor. The 1 GB/day cap matters more than the retention: this
// exercise runs at 100% sampling on purpose, so a stuck retry loop could emit a surprising amount
// in a short time, and the cap turns that into stopped ingestion rather than an invoice.
param retentionDays = 30
param dailyQuotaGb = 1

// Five percent. Low enough to catch a real regression, high enough that a single failed request
// in a quiet window does not fire — and the query carries a minimum-volume guard for the case
// the percentage alone cannot handle.
param errorRateThresholdPercent = 5
