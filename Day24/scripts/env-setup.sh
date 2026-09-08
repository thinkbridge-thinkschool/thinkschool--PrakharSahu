#!/usr/bin/env bash
#
# Day 24 — create an azd environment and give it the two identifiers no profile can carry.
#
#   ./scripts/env-setup.sh dev
#   ./scripts/env-setup.sh prod
#
# Idempotent. Safe to re-run; it reuses an existing azd environment rather than failing.
#
# ---------------------------------------------------------------------------------------------
# WHY THESE TWO VALUES ARE NOT IN A PROFILE
#
# infra/profiles/*.json describe SIZING — facts about how big and how durable an environment is,
# true for anyone who deploys this template. These two are not that. An Entra object id and a
# Container Apps environment resource id are specific to one directory and one subscription, so
# a committed value is wrong for everybody else who clones the repo.
#
# Neither is a secret. Both are public identifiers, which is why they can go in a .env file and
# be echoed to a terminal without care. They are absent from git for correctness, not safety.
# ---------------------------------------------------------------------------------------------
set -uo pipefail
export MSYS_NO_PATHCONV=1

ENVIRONMENT="${1:-dev}"
case "$ENVIRONMENT" in
  dev|prod) ;;
  *) echo "usage: $0 [dev|prod]" >&2; exit 2 ;;
esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.." || exit 1

LOCATION="${LOCATION:-centralindia}"

die() { printf '\n%s\n' "$*" >&2; exit 1; }

# ---- fail on the wrong subscription BEFORE anything is created ------------------------------
SUBSCRIPTION_ID="$(az account show --query id -o tsv)" || die "Not signed in. Run 'az login'."
SUBSCRIPTION_NAME="$(az account show --query name -o tsv)"
echo "Subscription: $SUBSCRIPTION_NAME ($SUBSCRIPTION_ID)"

if [ -n "${EXPECTED_SUBSCRIPTION_ID:-}" ] && [ "$SUBSCRIPTION_ID" != "$EXPECTED_SUBSCRIPTION_ID" ]; then
  die "Signed into $SUBSCRIPTION_ID but this targets $EXPECTED_SUBSCRIPTION_ID."
fi

# ---- the azd environment ---------------------------------------------------------------------
# `azd env new` fails if the environment exists, which is the wrong behaviour for a setup script
# somebody runs twice. Check first and select instead.
if azd env list --output json 2>/dev/null | grep -q "\"Name\": *\"$ENVIRONMENT\""; then
  echo "azd environment '$ENVIRONMENT' exists; selecting it."
  azd env select "$ENVIRONMENT" || die "Could not select the '$ENVIRONMENT' environment."
else
  echo "Creating azd environment '$ENVIRONMENT'."
  azd env new "$ENVIRONMENT" \
    --location "$LOCATION" \
    --subscription "$SUBSCRIPTION_ID" \
    --no-prompt || die "Could not create the '$ENVIRONMENT' environment."
fi

# ---------------------------------------------------------------------------------------------
# The SQL administrator.
#
# An Entra GROUP, never a person and never a password. A group survives someone leaving the
# team; a named individual becomes an orphaned admin the day they change roles.
#
# The group name comes from the profile, so dev and prod get DIFFERENT groups — sharing one
# means anyone who can break dev can break production, which quietly undoes the reason for
# having two environments.
# ---------------------------------------------------------------------------------------------
ADMIN_GROUP="$(python -c "
import json,io
print(json.load(io.open('infra/profiles/${ENVIRONMENT}.json', encoding='utf-8'))['sqlAdminLogin'])
")" || die "Could not read sqlAdminLogin from infra/profiles/${ENVIRONMENT}.json."

echo "SQL admin group (from the profile): $ADMIN_GROUP"

ADMIN_OBJECT_ID="$(az ad group show --group "$ADMIN_GROUP" --query id -o tsv 2>/dev/null)"

if [ -z "$ADMIN_OBJECT_ID" ]; then
  echo "  group not found; creating it."
  ADMIN_OBJECT_ID="$(az ad group create \
    --display-name "$ADMIN_GROUP" \
    --mail-nickname "$ADMIN_GROUP" \
    --query id -o tsv 2>/dev/null)"

  if [ -n "$ADMIN_OBJECT_ID" ]; then
    # The signed-in user has to be IN the admin group, or the deployment succeeds and nobody can
    # connect to the database it created. Entra-only auth means there is no password to fall
    # back on — an admin group with no members is an unreachable database.
    ME="$(az ad signed-in-user show --query id -o tsv)"
    az ad group member add --group "$ADMIN_OBJECT_ID" --member-id "$ME" -o none 2>/dev/null \
      && echo "  added the signed-in user as a member." \
      || echo "  NOTE: could not add you to the group. Add a member before connecting to SQL."
  else
    # Creating groups needs directory permissions a student or guest account often lacks. Fall
    # back to the signed-in user so the deployment can proceed, and say so plainly rather than
    # quietly producing a template with a placeholder admin.
    echo "  cannot create groups in this directory; falling back to the signed-in user."
    echo "  This is a REAL COMPROMISE: the SQL admin becomes a person, not a group."
    ADMIN_OBJECT_ID="$(az ad signed-in-user show --query id -o tsv)" \
      || die "Could not resolve the signed-in user either."
  fi
fi

# ---------------------------------------------------------------------------------------------
# The Container Apps environment.
#
# This subscription permits exactly ONE across the whole subscription — not one per region. So
# both dev and prod reuse whichever already exists, and discovering it rather than hard-coding
# the id keeps this portable to a subscription without that cap, where the value is empty and
# the template creates its own.
# ---------------------------------------------------------------------------------------------
CONTAINER_ENV_ID="$(az containerapp env list --query '[0].id' -o tsv 2>/dev/null)"
if [ -n "$CONTAINER_ENV_ID" ]; then
  echo "Reusing Container Apps environment: ${CONTAINER_ENV_ID##*/}"
else
  echo "No Container Apps environment found; the template will create one."
fi

# ---- write them into the azd environment ----------------------------------------------------
azd env set AZURE_ENV_NAME        "$ENVIRONMENT"     || die "azd env set failed."
azd env set AZURE_LOCATION        "$LOCATION"        || die "azd env set failed."
azd env set AZURE_SUBSCRIPTION_ID "$SUBSCRIPTION_ID" || die "azd env set failed."
azd env set SQL_ADMIN_OBJECT_ID   "$ADMIN_OBJECT_ID" || die "azd env set failed."
azd env set CONTAINER_APP_ENV_ID  "$CONTAINER_ENV_ID"

echo
echo "=== azd environment '$ENVIRONMENT' is ready ==="
azd env get-values | grep -v '^$'
echo
echo "Next:  azd provision --no-prompt"
