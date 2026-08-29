#!/usr/bin/env bash
#
# One-time setup so GitHub Actions can deploy without a stored credential.
#
# Federated identity in one paragraph: instead of handing GitHub a client secret, an app
# registration is told to trust tokens that GitHub itself issues, but only when the token's
# subject matches an exact repository, branch or environment. GitHub mints one of those
# tokens per run, Entra trades it for an Azure token, and nothing durable is stored on either
# side. There is no secret to leak, and a token stolen mid-run expires in minutes and only
# works from the workflow it was scoped to.
#
# Usage:
#   ./Day17/scripts/setup-github-oidc.sh <github-owner>/<repo>

set -euo pipefail
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

REPO="${1:-}"
[ -n "$REPO" ] || { echo "usage: $0 <owner>/<repo>"; exit 1; }

SUBSCRIPTION_ID="${SUBSCRIPTION_ID:-132ef106-f8ec-4352-83e4-9bc238274f25}"
TENANT_ID="${TENANT_ID:-8d46a076-d093-416d-a57b-8692cde13bf8}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-quotes-day17}"
CI_APP_NAME="${CI_APP_NAME:-github-day17-deployer}"
ENVIRONMENT_NAME="${ENVIRONMENT_NAME:-day17}"

az account set --subscription "$SUBSCRIPTION_ID"

APP_ID="$(az ad app list --display-name "$CI_APP_NAME" --query "[0].appId" -o tsv)"
if [ -z "$APP_ID" ] || [ "$APP_ID" = "null" ]; then
  APP_ID="$(az ad app create --display-name "$CI_APP_NAME" --query appId -o tsv)"
  echo "created app registration $APP_ID"
fi

SP_ID="$(az ad sp list --filter "appId eq '$APP_ID'" --query "[0].id" -o tsv)"
if [ -z "$SP_ID" ] || [ "$SP_ID" = "null" ]; then
  SP_ID="$(az ad sp create --id "$APP_ID" --query id -o tsv)"
fi

# Scoped to the one resource group these deployments touch, not the subscription. A CI
# identity that can only reach what it deploys is the difference between a bad workflow edit
# and a bad afternoon.
az role assignment create \
  --assignee-object-id "$SP_ID" --assignee-principal-type ServicePrincipal \
  --role Contributor \
  --scope "/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}" \
  >/dev/null 2>&1 || echo "role assignment already present"

# The subject must match exactly what GitHub puts in the token. `environment:day17` pairs
# with `environment: day17` in the workflow — a mismatch here fails at sign-in with a
# message that names neither side, so it is worth reading twice.
add_federated_credential() {
  local name="$1" subject="$2"
  local body; body="$(mktemp)"
  cat > "$body" <<JSON
{
  "name": "${name}",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "${subject}",
  "audiences": ["api://AzureADTokenExchange"],
  "description": "Day 17 deployment from ${REPO}"
}
JSON
  az ad app federated-credential create --id "$APP_ID" --parameters "@${body}" >/dev/null 2>&1 \
    && echo "added federated credential: ${subject}" \
    || echo "federated credential already present: ${subject}"
  rm -f "$body"
}

add_federated_credential "day17-environment" "repo:${REPO}:environment:${ENVIRONMENT_NAME}"
add_federated_credential "day17-main"        "repo:${REPO}:ref:refs/heads/main"

cat <<EOF

Done. Add these as repository VARIABLES (Settings -> Secrets and variables -> Actions ->
Variables). They are identifiers, not secrets:

  AZURE_CLIENT_ID       ${APP_ID}
  AZURE_TENANT_ID       ${TENANT_ID}
  AZURE_SUBSCRIPTION_ID ${SUBSCRIPTION_ID}

Then create an environment named '${ENVIRONMENT_NAME}' under Settings -> Environments.

Note what you are NOT adding: no AZURE_CREDENTIALS, no client secret, and no Static Web Apps
deployment token — the workflow fetches that at run time through this same OIDC session.
EOF
