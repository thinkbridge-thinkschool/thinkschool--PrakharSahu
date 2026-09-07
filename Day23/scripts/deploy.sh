#!/usr/bin/env bash
#
# Day 23 — deploy the Dispatch infrastructure.
#
# This one CREATES BILLABLE RESOURCES. It plans first, shows the plan, and refuses to proceed
# without an explicit confirmation — because "I ran the deploy script to see what it did" is how
# people find out what an S1 database costs.
#
#   ./scripts/deploy.sh dev
#
set -uo pipefail
export MSYS_NO_PATHCONV=1

ENVIRONMENT="${1:-dev}"
case "$ENVIRONMENT" in
  dev|prod) ;;
  *) echo "usage: $0 [dev|prod]" >&2; exit 2 ;;
esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INFRA_DIR="$(cd "$SCRIPT_DIR/../infra" && pwd)"
RESOURCE_GROUP="rg-dispatch-${ENVIRONMENT}"
LOCATION="${LOCATION:-centralindia}"
STAMP="$(date +%Y%m%d-%H%M%S)"

die() { printf '\n%s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------------------------
# Fail loudly on the wrong subscription BEFORE anything is submitted.
#
# Day 17's lesson: the managed identity and the app registration must live in the same tenant,
# and discovering that three steps into a deployment produces an error naming a resource rather
# than the actual problem. A check at the top costs one API call.
# ---------------------------------------------------------------------------------------------
CURRENT_SUB="$(az account show --query id -o tsv)" || die "Not signed in. Run 'az login'."
if [ -n "${EXPECTED_SUBSCRIPTION_ID:-}" ] && [ "$CURRENT_SUB" != "$EXPECTED_SUBSCRIPTION_ID" ]; then
  die "Signed into subscription $CURRENT_SUB but this deploy targets $EXPECTED_SUBSCRIPTION_ID."
fi

export SQL_ADMIN_OBJECT_ID="${SQL_ADMIN_OBJECT_ID:-$(az ad signed-in-user show --query id -o tsv)}"
export CONTAINER_APP_ENV_ID="${CONTAINER_APP_ENV_ID:-$(az containerapp env list --query '[0].id' -o tsv 2>/dev/null)}"

az group create -n "$RESOURCE_GROUP" -l "$LOCATION" -o none || die "Could not reach $RESOURCE_GROUP."

# ---- plan, then ask --------------------------------------------------------------------------
echo "=== Plan ($ENVIRONMENT) ==="
cd "$INFRA_DIR"
az deployment group what-if \
  --resource-group "$RESOURCE_GROUP" \
  --template-file main.bicep \
  --parameters "main.${ENVIRONMENT}.bicepparam" \
  --name "dispatch-${ENVIRONMENT}-plan" || die "Planning failed. Nothing was deployed."

echo
printf 'Deploy the above to %s? Type the environment name to confirm: ' "$RESOURCE_GROUP"
read -r CONFIRM
[ "$CONFIRM" = "$ENVIRONMENT" ] || die "Not confirmed. Nothing was deployed."

# ---- deploy -----------------------------------------------------------------------------------
# A unique deployment name per run, so the deployment history is a readable audit trail rather
# than one entry overwritten repeatedly.
echo
echo "=== Deploying ==="
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file main.bicep \
  --parameters "main.${ENVIRONMENT}.bicepparam" \
  --name "dispatch-${ENVIRONMENT}-${STAMP}" \
  --output json > "$SCRIPT_DIR/../docs/deploy-${ENVIRONMENT}.json" \
  || die "Deployment failed. See the output above."

echo "  deployed. Outputs written to docs/deploy-${ENVIRONMENT}.json"

# ---------------------------------------------------------------------------------------------
# The step Bicep cannot perform.
#
# Granting the managed identity a DATABASE user is a T-SQL statement executed inside the
# database — `CREATE USER ... FROM EXTERNAL PROVIDER`. There is no ARM resource for it, so a
# template alone leaves the API able to reach SQL and unable to read anything from it.
#
# The statement is emitted as a template output rather than living in somebody's notes. Running
# it needs a SQL client authenticated as the Entra admin, which is why it is printed rather than
# executed: `az` cannot run T-SQL, and adding sqlcmd as a hard dependency of this script would
# make the common path fail on a machine that does not have it.
# ---------------------------------------------------------------------------------------------
GRANT_SQL="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" \
  --name "dispatch-${ENVIRONMENT}-${STAMP}" \
  --query properties.outputs.grantDatabaseAccessScript.value -o tsv)"

SQL_FQDN="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" \
  --name "dispatch-${ENVIRONMENT}-${STAMP}" \
  --query properties.outputs.sqlServerFqdn.value -o tsv)"

cat <<EOF

=== ONE MANUAL STEP REMAINS ===

The API's managed identity exists and has Service Bus rights, but it is not yet a user in the
database. Run this against ${SQL_FQDN}, signed in as the Entra SQL admin:

  ${GRANT_SQL}

Until then the app starts, answers /health, and fails every query with "Login failed for user
'<token-identified principal>'" — which reads like a credential problem and is a missing GRANT.
EOF
