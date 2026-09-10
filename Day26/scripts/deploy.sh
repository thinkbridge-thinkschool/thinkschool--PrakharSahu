#!/usr/bin/env bash
#
# Day 26 — deploy the telemetry destination and the broker.
#
#   ./scripts/deploy.sh
#
# Creates: Log Analytics, Application Insights, a Standard Service Bus namespace with one topic
# and two subscriptions, the data-role assignments, and the error-rate alert rule.
#
# Cost is dominated by Service Bus Standard, roughly USD 0.013/hour. App Insights ingestion is
# free below 5 GB/month and this deployment caps the workspace at 1 GB/day.
set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.." || exit 1

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-observability-dev}"
LOCATION="${LOCATION:-centralindia}"
STAMP="$(date +%Y%m%d-%H%M%S)"

die() { printf '\n%s\n' "$*" >&2; exit 1; }

az account show >/dev/null 2>&1 || die "Not signed in. Run 'az login'."
echo "Subscription: $(az account show --query name -o tsv)"

# ---------------------------------------------------------------------------------------------
# The identity that will run the application.
#
# Service Bus has local auth disabled, so there is no connection string to fall back on and the
# process authenticates as whoever is signed in to the Azure CLI. That principal needs the data
# roles, which is what this object id is for. It is a public identifier, not a secret.
# ---------------------------------------------------------------------------------------------
export DEVELOPER_OBJECT_ID="${DEVELOPER_OBJECT_ID:-$(az ad signed-in-user show --query id -o tsv)}"
export LOCATION
[ -n "$DEVELOPER_OBJECT_ID" ] || die "Could not resolve the signed-in user."

echo "=== Compiling ==="
( cd infra && az bicep build --file main.bicep --stdout >/dev/null ) || die "Bicep failed to compile."
( cd infra && az bicep build-params --file main.bicepparam --stdout >/dev/null ) || die "Parameters do not satisfy main.bicep."
echo "  clean."

az group create -n "$RESOURCE_GROUP" -l "$LOCATION" -o none || die "Could not create $RESOURCE_GROUP."

echo
echo "=== Deploying ==="
( cd infra && az deployment group create \
    --resource-group "$RESOURCE_GROUP" \
    --template-file main.bicep \
    --parameters main.bicepparam \
    --name "observability-${STAMP}" \
    -o none ) || die "Deployment failed."
echo "  deployed."

OUT="$(az deployment group show -g "$RESOURCE_GROUP" -n "observability-${STAMP}" --query properties.outputs -o json)"
val() { printf '%s' "$OUT" | python -c "import json,sys;print(json.load(sys.stdin).get('$1',{}).get('value',''))"; }

APPI_CONN="$(val appInsightsConnectionString)"
SB_FQDN="$(val serviceBusFullyQualifiedNamespace)"
TOPIC="$(val serviceBusTopic)"
WORKSPACE_ID="$(val workspaceCustomerId)"
APPI_NAME="$(val appInsightsName)"

# ---------------------------------------------------------------------------------------------
# Write the local run configuration.
#
# .env rather than a committed file, and it is gitignored — not because these are secrets (the
# App Insights connection string carries an ingestion key that is write-only, and the Service Bus
# FQDN is a hostname) but because they name one specific deployment. A committed copy would be
# stale the moment anybody redeploys into a fresh resource group.
#
# Day 25 argued against .env for a cloud-hosted app, where App Service injects settings and a
# file would be a competing source of truth. That argument does not apply to a process running on
# a laptop: here there IS no platform to inject anything, and the alternative is exporting six
# variables by hand every time a shell is opened.
# ---------------------------------------------------------------------------------------------
# EVERY VALUE IS QUOTED, and that is load-bearing rather than tidy.
#
# run-trace.sh reads this file with `source`, which executes it as shell. An App Insights
# connection string contains semicolons:
#
#   InstrumentationKey=<guid>;IngestionEndpoint=https://...;LiveEndpoint=https://...
#
# Unquoted, bash reads the first `;` as a command separator: the variable is set to
# "InstrumentationKey=<guid>" and the rest is run as commands. The application then starts,
# reports `azureMonitor=enabled` because a connection string is present, and exports NOTHING -
# because the ingestion endpoint was silently cut off. Twelve traces were produced and lost
# exactly this way before the truncation was spotted.
cat > .env <<EOF
APPLICATIONINSIGHTS_CONNECTION_STRING="${APPI_CONN}"
ServiceBus__FullyQualifiedNamespace="${SB_FQDN}"
ServiceBus__TopicName="${TOPIC}"
ServiceBus__AuditSubscription="$(val serviceBusAuditSubscription)"
ServiceBus__SearchIndexSubscription="$(val serviceBusSearchSubscription)"

# Authenticate as the signed-in Azure CLI user rather than through the full credential chain.
# DefaultAzureCredential tries managed identity first, and off Azure that probes the link-local
# IMDS address, fails with AuthenticationFailedException rather than CredentialUnavailableException,
# and aborts the chain before it ever reaches the CLI credential. Symptom: an outbox that claims
# rows and never publishes them. Unset this when running in Azure.
ServiceBus__UseAzureCliCredential="true"
EOF

cat <<EOF

=============================================================================================
 Deployed
=============================================================================================

  app insights   ${APPI_NAME}
  workspace id   ${WORKSPACE_ID}
  service bus    ${SB_FQDN}
  topic          ${TOPIC}
  alert rule     $(val alertRuleName)

  wrote .env for the local run (gitignored)

Next:
  ./scripts/run-trace.sh      start the API and the worker, generate traffic
  ./scripts/query-kql.sh      run every query in kql/ against the workspace

EOF
