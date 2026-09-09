#!/usr/bin/env bash
#
# Day 25 — deploy the identity stack.
#
#   export ENTRA_CLIENT_ID=<from scripts/entra-app.sh>
#   ./scripts/deploy.sh
#
# Creates billable resources: a B1 App Service plan, a Standard Service Bus namespace, a
# serverless SQL database and a Key Vault. Roughly USD 0.03/hour, and the database auto-pauses.
#
# ---------------------------------------------------------------------------------------------
# THE ORDERING THIS SCRIPT EXISTS TO GET RIGHT
#
#   1. deploy the template          identity, vault, SQL, Service Bus, RBAC, then the app
#   2. write the secret VALUE       data plane only — never through ARM
#   3. restart the app              so the Key Vault reference resolves against a secret that
#                                   now exists
#   4. grant the identity in SQL    T-SQL, which no template can express
#
# Step 2 has to come after step 1 because the vault does not exist until then, and step 3 has to
# come after step 2 because App Service resolves Key Vault references at STARTUP. An app started
# before its secret existed caches a failed reference and does not retry on its own — the
# symptom is an app that was briefly broken and stays broken after the secret is added.
# ---------------------------------------------------------------------------------------------
set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.." || exit 1

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-identity-dev}"
LOCATION="${LOCATION:-centralindia}"
SECRET_NAME="${SECRET_NAME:-payments-webhook-signing-key}"
STAMP="$(date +%Y%m%d-%H%M%S)"

die() { printf '\n%s\n' "$*" >&2; exit 1; }

# ---- preconditions ---------------------------------------------------------------------------
az account show >/dev/null 2>&1 || die "Not signed in. Run 'az login'."
SUBSCRIPTION_ID="$(az account show --query id -o tsv)"
echo "Subscription: $(az account show --query name -o tsv) ($SUBSCRIPTION_ID)"

[ -n "${ENTRA_CLIENT_ID:-}" ] \
  || die "ENTRA_CLIENT_ID is not set. Run ./scripts/entra-app.sh first, then export it."

# ---------------------------------------------------------------------------------------------
# The SQL administrator.
#
# The signed-in user, and that is a COMPROMISE rather than a design. Production wants an Entra
# GROUP here: a group survives someone leaving the team, while a named individual becomes an
# orphaned admin the day they change roles. It is a person here because this is one machine and
# one directory, and saying so is better than a template that looks production-shaped and is not.
# ---------------------------------------------------------------------------------------------
export SQL_ADMIN_OBJECT_ID="${SQL_ADMIN_OBJECT_ID:-$(az ad signed-in-user show --query id -o tsv)}"
export SQL_ADMIN_LOGIN="${SQL_ADMIN_LOGIN:-$(az ad signed-in-user show --query userPrincipalName -o tsv)}"
export LOCATION
[ -n "$SQL_ADMIN_OBJECT_ID" ] || die "Could not resolve the signed-in user."
echo "SQL admin: the signed-in user (a group is the production answer)"

# ---- compile before submitting ---------------------------------------------------------------
echo
echo "=== Compiling ==="
( cd infra && az bicep build --file main.bicep --stdout >/dev/null ) \
  || die "Bicep failed to compile. Fix that before deploying."
( cd infra && az bicep build-params --file main.bicepparam --stdout >/dev/null ) \
  || die "The parameter file does not satisfy main.bicep."
echo "  template and parameters compile."

az group create -n "$RESOURCE_GROUP" -l "$LOCATION" -o none || die "Could not create $RESOURCE_GROUP."

# ---- plan ------------------------------------------------------------------------------------
echo
echo "=== Plan ==="
( cd infra && az deployment group what-if \
    --resource-group "$RESOURCE_GROUP" \
    --template-file main.bicep \
    --parameters main.bicepparam \
    --name "identity-plan" 2>&1 | tail -25 ) || die "Planning failed. Nothing was deployed."

# ---- deploy ----------------------------------------------------------------------------------
echo
echo "=== Deploying ==="
( cd infra && az deployment group create \
    --resource-group "$RESOURCE_GROUP" \
    --template-file main.bicep \
    --parameters main.bicepparam \
    --name "identity-${STAMP}" \
    -o none ) || die "Deployment failed. See the output above."
echo "  deployed."

# ---- read the outputs back -------------------------------------------------------------------
OUT="$(az deployment group show -g "$RESOURCE_GROUP" -n "identity-${STAMP}" --query properties.outputs -o json)"
val() { printf '%s' "$OUT" | python -c "import json,sys;print(json.load(sys.stdin).get('$1',{}).get('value',''))"; }

SITE_NAME="$(val siteName)"
SITE_URL="$(val siteUrl)"
KEY_VAULT="$(val keyVaultName)"
IDENTITY_NAME="$(val identityName)"
IDENTITY_CLIENT_ID="$(val identityClientId)"
SQL_FQDN="$(val sqlServerFqdn)"
SQL_DB="$(val sqlDatabaseName)"
GRANT_SQL="$(val grantDatabaseAccessScript)"

# ---------------------------------------------------------------------------------------------
# The secret VALUE. Generated here, written straight to the vault's data plane.
#
# It is worth being precise about what this step is and is not. This is a stand-in for a
# credential issued by a third party that does not federate — the case Key Vault genuinely
# exists for. A real one would be pasted from that provider's console; a random value is used
# here so that nothing resembling a real credential is ever committed or printed.
#
# `az keyvault secret set` talks to the vault's DATA plane. It never passes through ARM, so the
# value is absent from the deployment history, from any parameter, and from this repository. It
# exists in exactly two places: the vault, and the memory of this shell.
#
# The deploying user needs a data-plane role to write it, and being subscription Owner does not
# grant one — the vault uses RBAC, and management rights are not data rights. The grant below is
# to the human running this, not to the app.
# ---------------------------------------------------------------------------------------------
echo
echo "=== Writing the third-party secret to Key Vault ==="

ME="$(az ad signed-in-user show --query id -o tsv)"
VAULT_ID="$(az keyvault show -n "$KEY_VAULT" -g "$RESOURCE_GROUP" --query id -o tsv)"

az role assignment create \
  --assignee-object-id "$ME" --assignee-principal-type User \
  --role "Key Vault Secrets Officer" \
  --scope "$VAULT_ID" -o none 2>/dev/null \
  && echo "  granted yourself Key Vault Secrets Officer (data-plane write)" \
  || echo "  Key Vault Secrets Officer already held."

# RBAC takes a few seconds to propagate to the data plane. Retry rather than sleep-and-hope: a
# fixed sleep is either too short on a bad day or wasted on a good one.
SECRET_VALUE="$(python -c "import secrets;print(secrets.token_urlsafe(32))")"
for attempt in 1 2 3 4 5 6; do
  if az keyvault secret set --vault-name "$KEY_VAULT" --name "$SECRET_NAME" \
       --value "$SECRET_VALUE" -o none 2>/dev/null; then
    echo "  secret '${SECRET_NAME}' written (attempt ${attempt})"
    SECRET_OK=1
    break
  fi
  echo "  waiting for the role assignment to propagate (attempt ${attempt})..."
  sleep 10
done
unset SECRET_VALUE
[ "${SECRET_OK:-0}" = "1" ] || die "Could not write the secret. The app's Key Vault reference will not resolve."

# ---- restart so the reference resolves --------------------------------------------------------
echo
echo "=== Restarting the app so the Key Vault reference resolves ==="
az webapp restart -n "$SITE_NAME" -g "$RESOURCE_GROUP" -o none || echo "  restart failed; do it by hand."
echo "  restarted."

# ---- what remains ------------------------------------------------------------------------------
cat <<EOF

=============================================================================================
 Deployed
=============================================================================================

  site            ${SITE_URL}
  identity        ${IDENTITY_NAME}
  identity client ${IDENTITY_CLIENT_ID}
  key vault       ${KEY_VAULT}
  sql             ${SQL_FQDN} / ${SQL_DB}

--- ONE STEP REMAINS: the database grant ---

The identity can REACH SQL — that is an Entra token. Being allowed to read a TABLE is a
database-level grant, and there is no ARM resource for it:

  ${GRANT_SQL}

Run it with the tool that does not need sqlcmd installed:

  ./scripts/grant-sql.sh

Until then the app authenticates successfully and every query fails with
"Login failed for user '<token-identified principal>'" — which reads like a credential problem
and is a missing GRANT.

EOF
