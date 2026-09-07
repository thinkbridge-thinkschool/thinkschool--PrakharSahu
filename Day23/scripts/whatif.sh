#!/usr/bin/env bash
#
# Day 23 — plan the Dispatch infrastructure without changing anything.
#
# `what-if` is READ-ONLY. It submits the template to ARM, which resolves it against the current
# state of the resource group and reports what would change. Nothing is created, so this is safe
# to run against production and is the only honest way to review an infrastructure change.
#
#   ./scripts/whatif.sh dev
#   ./scripts/whatif.sh prod
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

die() { printf '\n%s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------------------------
# The two values that are NOT in the parameter files.
#
# Both are read by `readEnvironmentVariable` in the .bicepparam. Neither is a secret — an object
# id and a resource id are public identifiers — but both are specific to one directory and one
# subscription, so committing them would be wrong for anyone else who clones this.
# ---------------------------------------------------------------------------------------------
if [ -z "${SQL_ADMIN_OBJECT_ID:-}" ]; then
  echo "SQL_ADMIN_OBJECT_ID not set; falling back to the signed-in user."
  SQL_ADMIN_OBJECT_ID="$(az ad signed-in-user show --query id -o tsv)" \
    || die "Could not resolve the signed-in user. Run 'az login' first."
  export SQL_ADMIN_OBJECT_ID
fi

if [ -z "${CONTAINER_APP_ENV_ID:-}" ]; then
  # This subscription permits exactly ONE Container Apps environment, so both environments have
  # to share it. Discovering it rather than hard-coding the id keeps the script portable.
  CONTAINER_APP_ENV_ID="$(az containerapp env list --query '[0].id' -o tsv 2>/dev/null)"
  export CONTAINER_APP_ENV_ID
  [ -n "$CONTAINER_APP_ENV_ID" ] \
    && echo "Reusing existing Container Apps environment: ${CONTAINER_APP_ENV_ID##*/}" \
    || echo "No existing Container Apps environment; the template will create one."
fi

echo "=== Compiling ==="
# Build FIRST, and check its status directly. A syntax error caught here costs two seconds; the
# same error caught by ARM costs a round trip and an error message that names a line in the
# generated JSON rather than in the Bicep.
( cd "$INFRA_DIR" && az bicep build --file main.bicep --stdout >/dev/null ) \
  || die "Bicep failed to compile. Fix that before planning."

( cd "$INFRA_DIR" && az bicep build-params --file "main.${ENVIRONMENT}.bicepparam" --stdout >/dev/null ) \
  || die "The ${ENVIRONMENT} parameter file does not satisfy main.bicep."
echo "  template and ${ENVIRONMENT} parameters both compile."

# The resource group must exist for what-if to have anything to compare against. Creating one is
# free and idempotent, so it is done here rather than being a documented prerequisite people skip.
az group create -n "$RESOURCE_GROUP" -l "$LOCATION" -o none \
  || die "Could not create or reach $RESOURCE_GROUP."

echo
echo "=== Planning $ENVIRONMENT ==="
cd "$INFRA_DIR"
az deployment group what-if \
  --resource-group "$RESOURCE_GROUP" \
  --template-file main.bicep \
  --parameters "main.${ENVIRONMENT}.bicepparam" \
  --name "dispatch-${ENVIRONMENT}-whatif"
