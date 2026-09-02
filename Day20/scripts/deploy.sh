#!/usr/bin/env bash
#
# Day 17 — provision and deploy the whole thing.
#
# Idempotent: every step checks for what it is about to create and reuses it. Re-running
# after a failure resumes rather than duplicating, which matters because the Entra steps in
# the middle are the ones most likely to fail on permissions.
#
# Run from anywhere:  ./Day17/scripts/deploy.sh
#
# ---------------------------------------------------------------------------------------
# What it builds, and why the shape is what it is
#
#   browser ──► Static Web App (Free)          Angular bundle. Static files only.
#      │
#      └──────► quotes-bff  (Container App)    Holds a SYSTEM-ASSIGNED managed identity.
#                    │                          Mints a token per request from IMDS.
#                    └──► quotes-api (Container App)
#                                               Validates that token before serving /api/*.
#
# The broker is a Container App rather than a Static Web App managed function because
# managed functions cannot be given a managed identity — Static Web Apps' own identity is
# scoped to Key Vault secret retrieval, and "bring your own Functions app" needs the
# Standard plan. A Container App with a system-assigned identity costs nothing extra and
# sits in the environment the API already runs in.
# ---------------------------------------------------------------------------------------

set -euo pipefail

# Git Bash rewrites arguments that look like Unix paths into Windows paths, which corrupts
# every Azure resource id (/subscriptions/... becomes C:/Program Files/...). Off for the
# whole script.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DAY17_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

# Translates an MSYS path into one a native Windows process can open, and is a no-op on
# Linux and macOS.
#
# This is the other half of MSYS_NO_PATHCONV above, and the two pull in opposite directions.
# With conversion left on, Git Bash mangles Azure resource ids — /subscriptions/... becomes
# C:/Program Files/... — so it has to be off. But with it off, a genuine filesystem path like
# /d/ThinkBridge/Day17/backend reaches az, node and npx untranslated, and those are native
# Windows processes that resolve it against the current drive root instead. az reports
# "Impossible to find the source directory", naming a path that plainly exists.
#
# So: conversion off globally, applied explicitly to the arguments that are really paths.
winpath() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

# --- Configuration. Override any of these in the environment. ---------------------------
#
# Targets the Azure for Students subscription. The Week-1 API is deployed here from
# Day17/backend rather than reusing the instance in the personal subscription: a managed
# identity can only be issued tokens for applications registered in its OWN tenant, so
# splitting the identity and the app registration across two tenants cannot be made to work.
# Deploying the whole stack into one subscription also makes this reproducible from a clean
# clone, which the split arrangement never was.
SUBSCRIPTION_ID="${SUBSCRIPTION_ID:-132ef106-f8ec-4352-83e4-9bc238274f25}"
TENANT_ID="${TENANT_ID:-8d46a076-d093-416d-a57b-8692cde13bf8}"

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-quotes-day17}"
LOCATION="${LOCATION:-centralindia}"
CONTAINERAPP_ENV="${CONTAINERAPP_ENV:-cae-quotes-day17}"

API_APP_NAME="${API_APP_NAME:-quotes-api}"
BFF_APP_NAME="${BFF_APP_NAME:-quotes-bff}"
# Only used when Static Web Apps cannot be created in this subscription; see step 4.
WEB_APP_NAME="${WEB_APP_NAME:-quotes-web}"

SWA_NAME="${SWA_NAME:-swa-quotes-day17}"
# Static Web Apps is only offered in a handful of regions and centralindia is not one of
# them; the resource is a control-plane record anyway, since the content is served from the
# global edge. eastasia is the nearest.
SWA_LOCATION="${SWA_LOCATION:-eastasia}"

APP_REG_NAME="${APP_REG_NAME:-quotes-api-day17}"
APP_ROLE_NAME="${APP_ROLE_NAME:-Api.Invoke}"

log()  { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
die()  { printf '\n\033[1;31mFAILED: %s\033[0m\n' "$*" >&2; exit 1; }

# --- 0. Preflight -----------------------------------------------------------------------
log "Preflight"

command -v az   >/dev/null || die "az CLI not found."
command -v node >/dev/null || die "node not found."

az account show >/dev/null 2>&1 || die "Not signed in. Run: az login --tenant $TENANT_ID"
az account set --subscription "$SUBSCRIPTION_ID"

# The check that would have saved the most time. A managed identity lives in the tenant its
# subscription belongs to, and Entra will only issue it tokens for applications registered in
# that same tenant. Get this wrong and acquisition fails with AADSTS500011 — an error that
# names a resource, not a tenant, and reads like a typo in the App ID URI.
ACTUAL_TENANT="$(az account show --query tenantId -o tsv)"
[ "$ACTUAL_TENANT" = "$TENANT_ID" ] || die \
"Signed into tenant $ACTUAL_TENANT but this script targets $TENANT_ID.
   The managed identity and the app registration must live in the same tenant.
   Fix with:  az account set --subscription $SUBSCRIPTION_ID"

az extension add --name containerapp --upgrade --only-show-errors >/dev/null 2>&1 || true
for ns in Microsoft.App Microsoft.Web Microsoft.ContainerRegistry Microsoft.OperationalInsights; do
  state="$(az provider show -n "$ns" --query registrationState -o tsv 2>/dev/null || echo Unknown)"
  [ "$state" = "Registered" ] || { info "registering $ns…"; az provider register --namespace "$ns" --wait >/dev/null; }
done

info "subscription : $(az account show --query name -o tsv)"
info "tenant       : $TENANT_ID"
info "resource grp : $RESOURCE_GROUP ($LOCATION)"

# --- 1. Resource group and Container Apps environment ------------------------------------
log "Resource group and Container Apps environment"

az group create -n "$RESOURCE_GROUP" -l "$LOCATION" --only-show-errors >/dev/null
info "resource group ready"

# Azure caps a subscription at ONE Container Apps environment per region on this offering,
# and the cap is enforced at creation with MaxNumberOfRegionalEnvironmentsInSubExceeded. So
# reuse whatever already exists in this region rather than trying to create a second one —
# an environment is a shared boundary, not a per-project resource, and apps in it may live
# in a different resource group.
# `az` reports the display name ("Central India") while --location takes the slug
# ("centralindia"), so both sides are normalised before comparing.
CONTAINERAPP_ENV_ID="$(az containerapp env list --query "[].[id,location]" -o tsv 2>/dev/null \
  | awk -v want="$(printf '%s' "$LOCATION" | tr -d ' ' | tr '[:upper:]' '[:lower:]')" \
      '{ id=$1; $1=""; loc=tolower($0); gsub(/[ \t]/,"",loc); if (loc==want) { print id; exit } }' \
  || true)"

if [ -n "$CONTAINERAPP_ENV_ID" ] && [ "$CONTAINERAPP_ENV_ID" != "null" ]; then
  info "reusing existing environment: $(basename "$CONTAINERAPP_ENV_ID")"
else
  info "creating environment (this takes a few minutes)…"
  az containerapp env create -n "$CONTAINERAPP_ENV" -g "$RESOURCE_GROUP" -l "$LOCATION" \
    --only-show-errors >/dev/null
  CONTAINERAPP_ENV_ID="$(az containerapp env show -n "$CONTAINERAPP_ENV" -g "$RESOURCE_GROUP" \
    --query id -o tsv)"
fi
info "environment ready"

# --- 1b. Container registry --------------------------------------------------------------
#
# Created explicitly rather than letting `az containerapp up` conjure one. On this
# subscription an "Allowed resource deployment regions" policy restricts deployments to
# five regions, and `containerapp up` picks its own region for the registry it creates —
# which lands outside the allowlist and fails with RequestDisallowedByAzure naming a
# resource nobody asked for. Creating it here pins it to $LOCATION, which is allowed.
log "Container registry"

ACR_NAME="$(az acr list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null || true)"
if [ -z "$ACR_NAME" ] || [ "$ACR_NAME" = "null" ]; then
  ACR_NAME="quotesday17$(node -e 'console.log(require("crypto").randomBytes(4).toString("hex"))')"
  # admin-enabled false on purpose: admin credentials are a username and password that would
  # then have to be stored on each Container App as a registry secret. Image pull uses the
  # app's managed identity instead, which is the same mechanism the rest of this exercise is
  # about and leaves nothing to store.
  az acr create -n "$ACR_NAME" -g "$RESOURCE_GROUP" -l "$LOCATION" \
    --sku Basic --admin-enabled false >/dev/null
  info "created registry $ACR_NAME"
else
  info "reusing registry $ACR_NAME"
fi

ACR_SERVER="$(az acr show -n "$ACR_NAME" --query loginServer -o tsv)"
ACR_ID="$(az acr show -n "$ACR_NAME" --query id -o tsv)"
info "registry     : $ACR_SERVER"

# Builds an image and runs it as a Container App.
#
# Built locally and pushed, rather than with `az acr build`. ACR Tasks — the cloud build
# service `az acr build` and `az containerapp up --source` both rely on — is not permitted on
# this subscription at all: it fails with TasksOperationsNotAllowed and points at a support
# request, which is Azure's way of saying the offering does not include it. A local Docker
# daemon is therefore a hard requirement for deploying from a workstation. The GitHub Actions
# runner has one, so CI is unaffected.
#
# The optional third argument names a function to run immediately BEFORE the app is switched
# to the real image. Configuration that the container needs in order to start has to be in
# place by then. The API refuses to boot without Jwt__Key — deliberately, so it can never
# sign with a key someone left in source control — so setting that afterwards means the first
# revision on the real image crash-loops, and `az containerapp update --image` sits there
# waiting for a revision that is never going to become healthy.
deploy_container_app() {
  local name="$1" src="$2" hook="${3:-}" port="${4:-8080}"
  local tag="${name}:$(date +%s)"
  local image="${ACR_SERVER}/${tag}"

  command -v docker >/dev/null || die "docker not found, and ACR Tasks is unavailable on this subscription."
  docker info >/dev/null 2>&1 || die "The Docker daemon is not running. Start Docker Desktop and re-run."

  info "building ${tag} locally…"
  docker build -q -t "$image" "$(winpath "$src")" >/dev/null

  # Uses the signed-in Azure identity rather than registry admin credentials, which is why
  # the registry could be created with --admin-enabled false.
  az acr login -n "$ACR_NAME" >/dev/null
  info "pushing ${tag}…"
  docker push -q "$image" >/dev/null

  if az containerapp show -n "$name" -g "$RESOURCE_GROUP" >/dev/null 2>&1; then
    [ -n "$hook" ] && "$hook"
    az containerapp update -n "$name" -g "$RESOURCE_GROUP" --image "$image" >/dev/null
    # Asserted on every deploy, not only at creation. An app left on the bootstrap image's
    # port 80 by an interrupted run would otherwise stay unreachable for good, and the
    # symptom — ingress answering on a port nothing is listening on — looks like a crash.
    az containerapp ingress update -n "$name" -g "$RESOURCE_GROUP" --target-port "$port" >/dev/null
    return
  fi

  # First creation is a three-step dance, and the order is the whole point.
  #
  # The app must exist before it has a managed identity; the identity must exist before it
  # can be granted AcrPull; and the grant must exist before the app is pointed at a private
  # image. Creating it directly against "$image" inverts that — the very first pull happens
  # with no credentials, the revision never goes healthy, and the error surfaces as an
  # image-pull failure that says nothing about role assignments.
  #
  # So it boots on a public image it can always pull, and is switched over afterwards.
  info "bootstrapping ${name} on a public image…"
  az containerapp create -n "$name" -g "$RESOURCE_GROUP" \
    --environment "$CONTAINERAPP_ENV_ID" \
    --image mcr.microsoft.com/k8se/quickstart:latest \
    --system-assigned \
    --ingress external --target-port 80 >/dev/null

  local principal
  principal="$(az containerapp show -n "$name" -g "$RESOURCE_GROUP" \
    --query identity.principalId -o tsv)"

  az role assignment create --assignee-object-id "$principal" \
    --assignee-principal-type ServicePrincipal \
    --role AcrPull --scope "$ACR_ID" >/dev/null 2>&1 || true

  # Role assignments are eventually consistent, and a pull attempted inside that window is
  # rejected exactly as though the role were missing.
  sleep 30

  az containerapp registry set -n "$name" -g "$RESOURCE_GROUP" \
    --server "$ACR_SERVER" --identity system >/dev/null

  [ -n "$hook" ] && "$hook"

  info "switching ${name} to ${tag}…"
  az containerapp update -n "$name" -g "$RESOURCE_GROUP" --image "$image" >/dev/null
  az containerapp ingress update -n "$name" -g "$RESOURCE_GROUP" --target-port "$port" >/dev/null
}

# --- 2. The Week-1 API -------------------------------------------------------------------
log "Deploying the Week-1 API '$API_APP_NAME'"

API_EXISTED=false
az containerapp show -n "$API_APP_NAME" -g "$RESOURCE_GROUP" >/dev/null 2>&1 && API_EXISTED=true

# Runs before the API is switched to its real image, because the API will not start without
# a signing key and a revision that cannot start never reports healthy.
configure_api_settings() {
  # The signing key. This is the ONE credential in the system, it belongs to the API's own
  # user-session feature, and it has nothing to do with the managed-identity path — it signs
  # the tokens the API issues to humans. Generated here, stored as a Container Apps secret,
  # and never written to disk or to this repository.
  #
  # Regenerated only when absent: rotating it on every deploy would invalidate every live
  # session and every refresh cookie for no reason.
  if ! az containerapp secret show -n "$API_APP_NAME" -g "$RESOURCE_GROUP" \
        --secret-name jwt-key >/dev/null 2>&1; then
    local key
    key="$(node -e 'console.log(require("crypto").randomBytes(48).toString("base64"))')"
    az containerapp secret set -n "$API_APP_NAME" -g "$RESOURCE_GROUP" \
      --secrets "jwt-key=${key}" --only-show-errors >/dev/null
    unset key
    info "generated a new JWT signing key (stored as a Container Apps secret)"
  else
    info "reusing the existing JWT signing key"
  fi

  az containerapp update -n "$API_APP_NAME" -g "$RESOURCE_GROUP" \
    --set-env-vars "Jwt__Key=secretref:jwt-key" "ASPNETCORE_ENVIRONMENT=Production" \
    --only-show-errors >/dev/null
}

deploy_container_app "$API_APP_NAME" "$DAY17_DIR/backend" configure_api_settings

API_FQDN="$(az containerapp show -n "$API_APP_NAME" -g "$RESOURCE_GROUP" \
  --query "properties.configuration.ingress.fqdn" -o tsv)"
API_BASE="https://${API_FQDN}"
info "api          : $API_BASE"

# No seed account is configured on purpose. verify.sh registers a throwaway user through
# POST /api/auth/register, which returns a signed-in session — so there is no bootstrap
# password to generate, store, or leak.

# --- 3. Entra app registration representing the API -------------------------------------
log "Entra app registration '$APP_REG_NAME'"

APP_ID="$(az ad app list --display-name "$APP_REG_NAME" --query "[0].appId" -o tsv 2>/dev/null || true)"

if [ -z "$APP_ID" ] || [ "$APP_ID" = "null" ]; then
  APP_ID="$(az ad app create --display-name "$APP_REG_NAME" \
    --sign-in-audience AzureADMyOrg --query appId -o tsv)"
  info "created appId $APP_ID"
else
  info "reusing appId $APP_ID"
fi

APP_OBJECT_ID="$(az ad app show --id "$APP_ID" --query id -o tsv)"
IDENTIFIER_URI="api://${APP_ID}"

# allowedMemberTypes ["Application"] is the load-bearing part: a role limited to "User"
# cannot be assigned to a service principal at all, and the assignment call later fails with
# a message that never mentions why.
APP_ROLE_ID="$(az ad app show --id "$APP_ID" \
  --query "appRoles[?value=='${APP_ROLE_NAME}'].id | [0]" -o tsv 2>/dev/null || true)"

if [ -z "$APP_ROLE_ID" ] || [ "$APP_ROLE_ID" = "null" ]; then
  APP_ROLE_ID="$(node -e 'console.log(require("crypto").randomUUID())')"
  ROLE_BODY="$(mktemp)"
  cat > "$ROLE_BODY" <<JSON
{
  "identifierUris": ["${IDENTIFIER_URI}"],
  "appRoles": [
    {
      "id": "${APP_ROLE_ID}",
      "allowedMemberTypes": ["Application"],
      "displayName": "${APP_ROLE_NAME}",
      "value": "${APP_ROLE_NAME}",
      "description": "Allows a service to call the Quotes API on its own behalf.",
      "isEnabled": true
    }
  ]
}
JSON
  az rest --method PATCH \
    --uri "https://graph.microsoft.com/v1.0/applications/${APP_OBJECT_ID}" \
    --headers "Content-Type=application/json" \
    --body "@$(winpath "$ROLE_BODY")" >/dev/null
  rm -f "$ROLE_BODY"
  info "declared app role $APP_ROLE_NAME ($APP_ROLE_ID)"
else
  info "reusing app role $APP_ROLE_NAME ($APP_ROLE_ID)"
fi

# An application is only a template; the service principal is the object in this tenant that
# role assignments actually point at. Without it, the assignment below has no resourceId.
API_SP_OBJECT_ID="$(az ad sp list --filter "appId eq '${APP_ID}'" --query "[0].id" -o tsv 2>/dev/null || true)"
if [ -z "$API_SP_OBJECT_ID" ] || [ "$API_SP_OBJECT_ID" = "null" ]; then
  API_SP_OBJECT_ID="$(az ad sp create --id "$APP_ID" --query id -o tsv)"
  info "created service principal $API_SP_OBJECT_ID"
else
  info "reusing service principal $API_SP_OBJECT_ID"
fi

# --- 4. Static Web App (created first, so its hostname can be baked into CORS) -----------
log "Static Web App '$SWA_NAME'"

# Static Web Apps is attempted first and is the intended host. It is not always creatable:
# SWA exists in centralus, eastus2, westus2, westeurope and eastasia only, and a subscription
# carrying the sys.regionrestriction policy may permit none of those. On Azure for Students
# the permitted set is indonesiacentral, centralindia, malaysiawest, uaenorth and koreacentral
# — disjoint from SWA's, and the policy is Microsoft-locked (attempting to widen it returns
# UnauthorizedApplicationId even for a subscription Owner).
#
# When that happens the frontend falls back to nginx on Container Apps, which CAN run in an
# allowed region. The fallback serves the identical bundle with a hand-translated copy of
# staticwebapp.config.json, so the app and its Lighthouse score are measured for real. It is
# explicitly NOT the same thing as shipping on Static Web Apps, and the verification log says
# so rather than glossing it.
FRONTEND_MODE="swa"

if az staticwebapp show -n "$SWA_NAME" -g "$RESOURCE_GROUP" >/dev/null 2>&1; then
  info "reusing existing Static Web App"
elif az staticwebapp create -n "$SWA_NAME" -g "$RESOURCE_GROUP" \
       --location "$SWA_LOCATION" --sku Free --only-show-errors >/dev/null 2>/tmp/swa-create-err.txt; then
  info "created"
else
  FRONTEND_MODE="containerapp"
  printf '\033[1;33m'
  info "Static Web Apps could not be created in this subscription:"
  sed 's/^/      /' /tmp/swa-create-err.txt | head -3
  info ""
  info "Falling back to nginx on Container Apps so there is still a live URL to verify."
  info "The Static Web Apps configuration and CI remain in the repository, ready to run"
  info "against a subscription without the region restriction."
  printf '\033[0m'
fi

if [ "$FRONTEND_MODE" = "swa" ]; then
  SWA_HOSTNAME="$(az staticwebapp show -n "$SWA_NAME" -g "$RESOURCE_GROUP" \
    --query defaultHostname -o tsv)"
else
  # Bootstrapped now, before the broker, purely to learn its hostname — the broker's CORS
  # allowlist is exact-match and has to be given the real origin up front. The bundle it will
  # actually serve is built and pushed at the end, once the broker's own hostname is known.
  if ! az containerapp show -n "$WEB_APP_NAME" -g "$RESOURCE_GROUP" >/dev/null 2>&1; then
    info "bootstrapping the static host…"
    az containerapp create -n "$WEB_APP_NAME" -g "$RESOURCE_GROUP" \
      --environment "$CONTAINERAPP_ENV_ID" \
      --image mcr.microsoft.com/k8se/quickstart:latest \
      --system-assigned \
      --ingress external --target-port 80 >/dev/null
    WEB_PRINCIPAL="$(az containerapp show -n "$WEB_APP_NAME" -g "$RESOURCE_GROUP" \
      --query identity.principalId -o tsv)"
    az role assignment create --assignee-object-id "$WEB_PRINCIPAL" \
      --assignee-principal-type ServicePrincipal \
      --role AcrPull --scope "$ACR_ID" >/dev/null 2>&1 || true
    sleep 20
    az containerapp registry set -n "$WEB_APP_NAME" -g "$RESOURCE_GROUP" \
      --server "$ACR_SERVER" --identity system >/dev/null
  fi
  SWA_HOSTNAME="$(az containerapp show -n "$WEB_APP_NAME" -g "$RESOURCE_GROUP" \
    --query "properties.configuration.ingress.fqdn" -o tsv)"
fi

SWA_ORIGIN="https://${SWA_HOSTNAME}"
info "origin       : $SWA_ORIGIN"

# --- 5. The BFF Container App, with a system-assigned managed identity -------------------
log "BFF Container App '$BFF_APP_NAME'"

# Same reason as configure_api_settings: bff/src/config.js calls required() for each of
# these and throws at startup if any is missing, so they must exist before the real image
# runs. That strictness is deliberate — a proxy that starts with no upstream configured
# would answer requests by failing them, which is worse than not starting.
#
# Note what is absent: no --secrets flag. Every value here is a public identifier or a
# hostname.
configure_bff_settings() {
  az containerapp update -n "$BFF_APP_NAME" -g "$RESOURCE_GROUP" \
    --set-env-vars \
      "UPSTREAM_API_BASE=${API_BASE}" \
      "API_APP_ID_URI=${IDENTIFIER_URI}" \
      "ALLOWED_ORIGINS=${SWA_ORIGIN}" \
    --only-show-errors >/dev/null

  # Idempotent, and needed for an app that predates the --system-assigned flag on create.
  az containerapp identity assign -n "$BFF_APP_NAME" -g "$RESOURCE_GROUP" \
    --system-assigned --only-show-errors >/dev/null
}

deploy_container_app "$BFF_APP_NAME" "$DAY17_DIR/bff" configure_bff_settings

BFF_PRINCIPAL_ID="$(az containerapp show -n "$BFF_APP_NAME" -g "$RESOURCE_GROUP" \
  --query "identity.principalId" -o tsv)"
BFF_FQDN="$(az containerapp show -n "$BFF_APP_NAME" -g "$RESOURCE_GROUP" \
  --query "properties.configuration.ingress.fqdn" -o tsv)"
BFF_ORIGIN="https://${BFF_FQDN}"

info "identity     : $BFF_PRINCIPAL_ID"
info "origin       : $BFF_ORIGIN"

# --- 6. Grant the managed identity the app role -----------------------------------------
log "Granting '$APP_ROLE_NAME' to the BFF's managed identity"

EXISTING_ASSIGNMENT="$(az rest --method GET \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/${BFF_PRINCIPAL_ID}/appRoleAssignments" \
  --query "value[?appRoleId=='${APP_ROLE_ID}'] | [0].id" -o tsv 2>/dev/null || true)"

if [ -z "$EXISTING_ASSIGNMENT" ] || [ "$EXISTING_ASSIGNMENT" = "null" ]; then
  ASSIGN_BODY="$(mktemp)"
  cat > "$ASSIGN_BODY" <<JSON
{
  "principalId": "${BFF_PRINCIPAL_ID}",
  "resourceId": "${API_SP_OBJECT_ID}",
  "appRoleId": "${APP_ROLE_ID}"
}
JSON
  # Entra takes a moment to make a brand-new managed identity's service principal visible to
  # Graph, and the assignment 404s until it is. Retrying is not paranoia; it fires most times
  # the identity was created in this same run.
  for attempt in 1 2 3 4 5 6; do
    if az rest --method POST \
        --uri "https://graph.microsoft.com/v1.0/servicePrincipals/${BFF_PRINCIPAL_ID}/appRoleAssignments" \
        --headers "Content-Type=application/json" \
        --body "@$(winpath "$ASSIGN_BODY")" >/dev/null 2>&1; then
      info "granted"
      break
    fi
    [ "$attempt" -eq 6 ] && die "Could not assign the app role after 6 attempts."
    info "identity not yet visible to Graph, retrying ($attempt/6)…"
    sleep 10
  done
  rm -f "$ASSIGN_BODY"
else
  info "already granted"
fi

# --- 7. Turn on caller-identity enforcement in the API -----------------------------------
log "Enabling caller-identity enforcement on '$API_APP_NAME'"

# Set AFTER the role assignment, so there is never a window in which the API is locked and
# the broker cannot yet open it.
az containerapp update -n "$API_APP_NAME" -g "$RESOURCE_GROUP" \
  --set-env-vars \
    "CallerIdentity__TenantId=${TENANT_ID}" \
    "CallerIdentity__Audience=${IDENTIFIER_URI}" \
    "CallerIdentity__RequiredRole=${APP_ROLE_NAME}" \
  --only-show-errors >/dev/null
info "audience     : $IDENTIFIER_URI"
info "required role: $APP_ROLE_NAME"

# --- 8. Build the frontend against the real BFF hostname --------------------------------
log "Building the Angular app"

node "$(winpath "$SCRIPT_DIR/set-bff-url.mjs")" "$BFF_ORIGIN"

if [ "$FRONTEND_MODE" = "swa" ]; then
  pushd "$DAY17_DIR/frontend" >/dev/null
  [ -d node_modules ] || npm ci
  npx ng build --configuration production
  popd >/dev/null
fi

# --- 9. Publish the frontend -------------------------------------------------------------
if [ "$FRONTEND_MODE" = "swa" ]; then
  log "Deploying to Static Web Apps"

  # The deployment token is fetched at run time and never written to disk or to a settings
  # blade. In CI the same value comes from the OIDC session; it exists nowhere in this repo.
  SWA_TOKEN="$(az staticwebapp secrets list -n "$SWA_NAME" -g "$RESOURCE_GROUP" \
    --query "properties.apiKey" -o tsv)"

  npx --yes @azure/static-web-apps-cli@latest deploy \
    "$(winpath "$DAY17_DIR/frontend/dist/quotes-web/browser")" \
    --deployment-token "$SWA_TOKEN" \
    --env production
else
  log "Publishing the frontend to the fallback static host"

  # The Angular build happens inside the image, after set-bff-url.mjs has stamped the broker
  # hostname into environment.production.ts — so the bundle in the image is already pointed at
  # the right place.
  configure_web_settings() {
    az containerapp update -n "$WEB_APP_NAME" -g "$RESOURCE_GROUP" \
      --set-env-vars "BFF_ORIGIN=${BFF_ORIGIN}" --only-show-errors >/dev/null
  }

  deploy_container_app "$WEB_APP_NAME" "$DAY17_DIR/frontend" configure_web_settings 80
fi

# --- 10. Summary -------------------------------------------------------------------------
log "Done"

cat > "$DAY17_DIR/.deploy-output.json" <<JSON
{
  "frontendMode": "${FRONTEND_MODE}",
  "swaUrl": "${SWA_ORIGIN}",
  "bffUrl": "${BFF_ORIGIN}",
  "apiUrl": "${API_BASE}",
  "subscriptionId": "${SUBSCRIPTION_ID}",
  "tenantId": "${TENANT_ID}",
  "resourceGroup": "${RESOURCE_GROUP}",
  "apiAppId": "${APP_ID}",
  "apiAppIdUri": "${IDENTIFIER_URI}",
  "appRole": "${APP_ROLE_NAME}",
  "appRoleId": "${APP_ROLE_ID}",
  "bffPrincipalId": "${BFF_PRINCIPAL_ID}"
}
JSON

info "frontend : $SWA_ORIGIN"
info "bff      : $BFF_ORIGIN"
info "api      : $API_BASE"
info ""
info "Verify with: ./Day17/scripts/verify.sh"
