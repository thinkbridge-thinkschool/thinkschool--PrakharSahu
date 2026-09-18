#!/usr/bin/env bash
#
# Stop paying for it.
#
#   bash scripts/teardown.sh          # ask first
#   bash scripts/teardown.sh --yes    # do not ask
#
# ---------------------------------------------------------------------------------------------------
# WHAT THIS DELETES, AND WHAT IT DELIBERATELY DOES NOT
#
# Deletes: the resource group rg-ship-day32 and everything in it — four container apps, the SQL
# server and both databases, the Key Vault, the managed identity.
#
# Leaves alone, because they belong to other days and other things depend on them:
#
#   quotesday17acr22887        the registry, shared with Day 17
#   thinkschool-env            the only Container Apps environment this subscription may have
#   sb-quotes-dev-6bi37i       Day 26's Service Bus namespace, borrowed for the quote-events topic
#   the Entra app registration Day 17's quotes-api-day17, still guarding Day 17's own deployment
#
# The images stay in the registry too. They cost a few megabytes of a registry that is already paid
# for, and having them there is what makes a redeploy a single `deploy.sh` rather than a rebuild.
#
# The app-role assignment granted to this deployment's identity disappears with the identity, so
# there is nothing to clean up in the directory.

set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

ASSUME_YES=false
[ "${1:-}" = "--yes" ] && ASSUME_YES=true

az account show >/dev/null 2>&1 || die "Not signed in. Run: az login"
az account set --subscription "$SUBSCRIPTION_ID"

az group show -n "$RESOURCE_GROUP" >/dev/null 2>&1 || {
  ok "$RESOURCE_GROUP does not exist — nothing to tear down."
  exit 0
}

step "About to delete $RESOURCE_GROUP"
az resource list -g "$RESOURCE_GROUP" --query "[].{name:name, type:type}" -o table

VAULT="$(az keyvault list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null || true)"

if [ "$ASSUME_YES" != true ]; then
  printf '\n    Type the group name to confirm: '
  read -r CONFIRM
  [ "$CONFIRM" = "$RESOURCE_GROUP" ] || die "Not confirmed. Nothing deleted."
fi

step "Deleting"
az group delete -n "$RESOURCE_GROUP" --yes --no-wait
ok "delete started (running in the background)"

# Key Vault soft-delete outlives the group. The vault name stays reserved for the full retention
# window, and a redeploy inside it fails with "vault name is not available" for a vault that is
# already gone — Day 27 lost time to exactly this. Purging is only possible because the template
# leaves purge protection off, which is the deliberate trade documented in modules/keyvault.bicep.
if [ -n "$VAULT" ]; then
  step "Purging the Key Vault so the name is reusable"
  info "waiting for the group delete to release $VAULT"
  for _ in $(seq 1 60); do
    az keyvault show -n "$VAULT" >/dev/null 2>&1 || break
    sleep 10
  done
  az keyvault purge -n "$VAULT" --location "$LOCATION" --no-wait 2>/dev/null \
    && ok "$VAULT purged" \
    || warn "$VAULT could not be purged yet. Run: az keyvault purge -n $VAULT --location $LOCATION"
fi

step "Done"
cat <<'SUMMARY'

    Deleted   rg-ship-day32 and everything in it
    Kept      the registry, the Container Apps environment, Day 26's Service Bus,
              the Day 17 Entra app registration, and the four images

    Redeploy  bash scripts/deploy.sh          (images are still in the registry)

SUMMARY
