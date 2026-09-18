#!/usr/bin/env bash
#
# Ship it.
#
#   bash scripts/build-images.sh   # first, always — the template references image tags
#   bash scripts/deploy.sh
#
# Idempotent: run it twice and the second run changes nothing. Every step either checks for what it
# is about to create or uses a deterministic name that makes the create a no-op.
#
# ---------------------------------------------------------------------------------------------------
# THE FOUR THINGS ARM CANNOT DO, WHICH IS WHY THIS SCRIPT EXISTS AT ALL
#
#   1. Mint a signing key. A secret has to come from somewhere, and a template that generates one
#      writes it into deployment history in plain text.
#   2. Grant an Entra app role. That is a Microsoft Graph operation, not an ARM one.
#   3. Create a database USER. A login is server-level and reachable from ARM; a user is a row
#      inside the database and only T-SQL can write it.
#   4. Apply EF migrations. Nothing outside the application knows what the schema should be.
#
# Everything else is in infra/. When a deployment needs a script, it is worth being explicit about
# which parts genuinely could not have been declarative.

set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_tool az     "Install the Azure CLI."
require_tool dotnet "Install the .NET 10 SDK."

step "Checking the session"
az account show >/dev/null 2>&1 || die "Not signed in. Run: az login"
az account set --subscription "$SUBSCRIPTION_ID"
ACCOUNT_NAME="$(az account show --query name -o tsv)"
TENANT_ID="$(az account show --query tenantId -o tsv)"
ADMIN_OBJECT_ID="$(az ad signed-in-user show --query id -o tsv)"
ADMIN_LOGIN="$(az ad signed-in-user show --query userPrincipalName -o tsv)"
ok "$ACCOUNT_NAME"
# The sign-in name is shown once, here, because getting the SQL administrator wrong is the failure
# that deploys cleanly and then refuses every login. It is not written to any file.
info "administrator  $ADMIN_LOGIN"

step "Resolving what we reuse"
ENV_ID="$(env_resource_id)"
ENV_DOMAIN="$(env_default_domain)"
[ -n "$ENV_ID" ] && [ -n "$ENV_DOMAIN" ] || die "Container Apps environment $MANAGED_ENV_NAME not found in $MANAGED_ENV_RG."
info "environment   $MANAGED_ENV_NAME  ($ENV_DOMAIN)"
info "registry      $ACR_NAME"
info "service bus   $SERVICEBUS_NAMESPACE"

for repo in dispatch-api quotes-api-day32 quotes-bff-day32 quotes-web-day32; do
  az acr repository show-tags -n "$ACR_NAME" --repository "$repo" -o tsv 2>/dev/null | grep -qx "$IMAGE_TAG" \
    || die "$repo:$IMAGE_TAG is missing from the registry. Run scripts/build-images.sh first."
done
ok "all four images present at :$IMAGE_TAG"

# ---------------------------------------------------------------------------------------------------
step "Collecting the two secrets"
#
# Neither is ever printed, written to a file, or passed as a command-line argument — arguments are
# visible to any process that can read /proc or run `ps`. They go into shell variables and from
# there into Bicep @secure() parameters via a parameter file on stdin.

# uniqueString() is an ARM function, so the vault's real name is only known after the template runs.
# On a REdeploy it already exists, and the signing key must not change — rotating it silently would
# invalidate every token every signed-in user holds. So: look for a vault with our tag, and reuse
# its key if there is one.
EXISTING_VAULT="$(az keyvault list -g "$RESOURCE_GROUP" --query "[?starts_with(name,'kv-ship-')].name | [0]" -o tsv 2>/dev/null || true)"

JWT_KEY=""
if [ -n "$EXISTING_VAULT" ]; then
  JWT_KEY="$(az keyvault secret show --vault-name "$EXISTING_VAULT" -n jwt-signing-key --query value -o tsv 2>/dev/null || true)"
fi

if [ -n "$JWT_KEY" ]; then
  ok "signing key    reused from $EXISTING_VAULT (rotating it would sign every user out)"
else
  # 48 bytes of CSPRNG output, base64. HMAC-SHA256 keys longer than the 32-byte block size are
  # hashed down to it, so more than 32 buys nothing; 48 is a round number comfortably above the
  # floor. openssl is used in preference to $RANDOM, which is neither cryptographic nor 48 bytes.
  JWT_KEY="$(openssl rand -base64 48)"
  ok "signing key    generated (48 bytes, CSPRNG)"
fi

# Telemetry. Not a secret -- an App Insights connection string carries an ingestion key that can
# only WRITE, and Azure prints it in the portal. Reused rather than created: a second component
# would split one system's traces across two places, which is worse than having none, because
# you would look in the wrong one and conclude nothing was instrumented.
APPINSIGHTS_CONNECTION="$(az monitor app-insights component show \
  --app "$APPINSIGHTS_NAME" -g "$APPINSIGHTS_RG" --query connectionString -o tsv 2>/dev/null || true)"
if [ -n "$APPINSIGHTS_CONNECTION" ]; then
  ok "telemetry      $APPINSIGHTS_NAME (Day 26)"
else
  warn "no Application Insights found -- the API will start with telemetry disabled"
fi

# No Service Bus credential is collected, and that is deliberate.
#
# The first version of this script read the namespace's SAS connection string and stored it in Key
# Vault. Every consumer then failed on a loop with "LocalAuthDisabled: SAS authentication has been
# disabled for the namespace" -- Day 26 turned local auth off on purpose. The fix was not a better
# hiding place for the credential; it was to stop having one. The API now reaches the broker with
# the same managed identity it uses for SQL, and the template grants it Send and Receive.
ok "service bus    $SERVICEBUS_NAMESPACE (RBAC, no SAS key)"

# Dispatch's audience, resolved by display name. Absent, the template leaves authentication off --
# so this refuses rather than shipping an open API, which is the whole point of having added it.
DISPATCH_APP_ID="$(az ad app list --display-name "$DISPATCH_APP_NAME" --query "[0].appId" -o tsv 2>/dev/null || true)"
[ -n "$DISPATCH_APP_ID" ] || die "$DISPATCH_APP_NAME not found. Run: bash scripts/setup-dispatch-entra.sh"
DISPATCH_AUDIENCE="api://$DISPATCH_APP_ID"
ok "dispatch auth  $DISPATCH_AUDIENCE"

# ---------------------------------------------------------------------------------------------------
step "Deploying the template"
DEPLOYMENT_NAME="day32-ship"

az deployment sub create \
  --name "$DEPLOYMENT_NAME" \
  --location "$LOCATION" \
  --template-file "$(winpath "$ROOT/infra/main.bicep")" \
  --parameters \
      location="$LOCATION" \
      resourceGroupName="$RESOURCE_GROUP" \
      sqlAdminObjectId="$ADMIN_OBJECT_ID" \
      sqlAdminLogin="$ADMIN_LOGIN" \
      sqlAdminPrincipalType=User \
      managedEnvironmentId="$ENV_ID" \
      environmentDefaultDomain="$ENV_DOMAIN" \
      containerRegistryName="$ACR_NAME" \
      containerRegistryResourceGroup="$ACR_RG" \
      imageTag="$IMAGE_TAG" \
      quotesApiAppIdUri="$QUOTES_API_APP_ID_URI" \
      quotesApiTenantId="$TENANT_ID" \
      quotesApiRequiredRole="$QUOTES_API_ROLE" \
      appInsightsConnectionString="$APPINSIGHTS_CONNECTION" \
      dispatchAuthAudience="$DISPATCH_AUDIENCE" \
      jwtSigningKey="$JWT_KEY" \
      serviceBusNamespaceName="$SERVICEBUS_NAMESPACE" \
      serviceBusResourceGroup="$SERVICEBUS_RG" \
  --output none \
  || die "Template deployment failed. 'az deployment sub show -n $DEPLOYMENT_NAME' has the detail."

read_output() {
  az deployment sub show -n "$DEPLOYMENT_NAME" --query "properties.outputs.$1.value" -o tsv
}

WEB_URL="$(read_output quotesWebUrl)"
BFF_URL="$(read_output quotesBffUrl)"
API_URL="$(read_output quotesApiUrl)"
DISPATCH_URL="$(read_output dispatchApiUrl)"
SQL_FQDN="$(read_output sqlServerFqdn)"
IDENTITY_NAME="$(read_output identityName)"
IDENTITY_CLIENT_ID="$(read_output identityClientId)"
IDENTITY_PRINCIPAL_ID="$(read_output identityPrincipalId)"
VAULT_NAME="$(read_output vaultName)"
ok "deployed"

# ---------------------------------------------------------------------------------------------------
step "Granting the Entra app role"
#
# The Quotes API refuses any /api/* request whose X-Caller-Token is not an app-only token carrying
# Api.Invoke for its audience. That grant is a Microsoft Graph write, invisible to ARM.
#
# It also needs the identity's SERVICE PRINCIPAL object id, which for a user-assigned managed
# identity is the same value as its principalId — one of the few places where two of its three ids
# coincide, and a good way to be lulled into thinking they always do.

API_SP_ID="$(az ad sp show --id "$QUOTES_API_APP_ID" --query id -o tsv)"
ROLE_ID="$(az ad app show --id "$QUOTES_API_APP_ID" --query "appRoles[?value=='$QUOTES_API_ROLE'].id | [0]" -o tsv)"
[ -n "$API_SP_ID" ] && [ -n "$ROLE_ID" ] || die "Could not resolve the $QUOTES_API_ROLE role on app $QUOTES_API_APP_ID."

ALREADY="$(az rest --method GET \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$IDENTITY_PRINCIPAL_ID/appRoleAssignments" \
  --query "value[?appRoleId=='$ROLE_ID'] | [0].id" -o tsv 2>/dev/null || true)"

if [ -n "$ALREADY" ]; then
  ok "$QUOTES_API_ROLE already granted"
else
  az rest --method POST \
    --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$IDENTITY_PRINCIPAL_ID/appRoleAssignments" \
    --headers 'Content-Type=application/json' \
    --body "{\"principalId\":\"$IDENTITY_PRINCIPAL_ID\",\"resourceId\":\"$API_SP_ID\",\"appRoleId\":\"$ROLE_ID\"}" \
    --output none \
    || die "Could not grant $QUOTES_API_ROLE. You must own the app registration $QUOTES_API_APP_ID to do this."
  ok "$QUOTES_API_ROLE granted to $IDENTITY_NAME"
  # Entra app-role assignments are not instant. A token minted in the next few seconds can still be
  # missing the role, and the API would reject it with a message that blames the token rather than
  # the clock.
  info "waiting 30s for the directory to replicate"
  sleep 30
fi

# ---------------------------------------------------------------------------------------------------
step "Opening SQL to this machine, temporarily"
#
# The next two steps run T-SQL from here, and the server's only firewall rule allows Azure services.
# The rule is named after the address so a stale one is obvious, and it is removed on the way out —
# including if a later step fails.

MY_IP="$(curl -s --max-time 20 https://api.ipify.org || true)"
[ -n "$MY_IP" ] || die "Could not determine this machine's public IP."
FW_RULE="operator-$(printf '%s' "$MY_IP" | tr '.' '-')"
SQL_SERVER_NAME="$(read_output sqlServerName)"

cleanup_firewall() {
  az sql server firewall-rule delete -n "$FW_RULE" -s "$SQL_SERVER_NAME" -g "$RESOURCE_GROUP" \
    --output none 2>/dev/null || true
}
trap cleanup_firewall EXIT

az sql server firewall-rule create -n "$FW_RULE" -s "$SQL_SERVER_NAME" -g "$RESOURCE_GROUP" \
  --start-ip-address "$MY_IP" --end-ip-address "$MY_IP" --output none
ok "$FW_RULE (removed when this script exits)"

# ---------------------------------------------------------------------------------------------------
step "Granting the identity a database user"
#
# CLIENT id, not principal id. SQL derives an external user's SID from the client id, so using the
# principal id creates a user that exists, authenticates, and is still refused with
#
#   Login failed for user '<token-identified principal>'
#
# — the user is real, the token is real, and their SIDs do not match. That mistake cost more time
# than any other in this repository, which is why the tool takes the client id as a named argument
# and validates that it is a GUID.

for db in quotes dispatch; do
  # No --nologo. `dotnet run` does not recognise it and forwards it to the application as the
  # first argument, which shifts every real argument one place right: the tool then read the
  # identity's NAME where it expected its client id and refused it as 'not a GUID'. The
  # argument validation is the only reason that surfaced as a clear message rather than as a
  # database user created with a SID made from the wrong bytes.
  dotnet run --project "$(winpath "$ROOT/tools/SqlGrant")" -c Release -- \
    "$SQL_FQDN" "$db" "$IDENTITY_NAME" "$IDENTITY_CLIENT_ID" \
    || die "Granting a user in [$db] failed."
  ok "[$db] user created for $IDENTITY_NAME"
done

# ---------------------------------------------------------------------------------------------------
step "Applying migrations"
#
# A deployment step, not a startup step. Both applications can migrate on boot and both default to
# not doing it: replicas starting together race on the migration lock, and a destructive migration
# would be applied by whichever one won with no human in the loop.
#
# Run as the signed-in administrator over `Active Directory Default` — the managed identity has a
# database user but only the rights that user was granted, and schema changes are deliberately not
# among them.

dotnet ef --version >/dev/null 2>&1 || {
  info "installing the dotnet-ef global tool"
  dotnet tool install --global dotnet-ef >/dev/null 2>&1 || dotnet tool update --global dotnet-ef >/dev/null 2>&1     || die "Could not install dotnet-ef. Install it with: dotnet tool install --global dotnet-ef"
  export PATH="$PATH:$HOME/.dotnet/tools"
}

migrate() {
  local label="$1" project="$2" startup="$3" context="$4" db="$5" connection_name="$6"
  local conn="Server=tcp:$SQL_FQDN,1433;Initial Catalog=$db;Encrypt=True;TrustServerCertificate=False;Connection Timeout=120;Authentication=Active Directory Default"
  info "$label -> [$db] ($context)"
  env "ConnectionStrings__$connection_name=$conn" dotnet ef database update \
    --project "$(winpath "$project")" --startup-project "$(winpath "$startup")" --context "$context" --no-build 2>&1 \
    | grep -Ev '^(Build started|Build succeeded)' | tail -3
}

dotnet build "$(winpath "$ROOT/apps/dispatch/Dispatch.slnx")" -c Debug --nologo -v q >/dev/null
dotnet build "$(winpath "$ROOT/apps/quotes/backend/QuotesApi.slnx")" -c Debug --nologo -v q >/dev/null

D="$ROOT/apps/dispatch"
migrate "dispatch" "$D/src/Modules/WorkManagement/Dispatch.WorkManagement.Infrastructure" "$D/src/Dispatch.Api" WorkManagementDbContext dispatch Dispatch
migrate "dispatch" "$D/src/Modules/Scheduling/Dispatch.Scheduling.Infrastructure"         "$D/src/Dispatch.Api" SchedulingDbContext     dispatch Dispatch
migrate "dispatch" "$D/src/Modules/Billing/Dispatch.Billing.Infrastructure"               "$D/src/Dispatch.Api" BillingDbContext        dispatch Dispatch

Q="$ROOT/apps/quotes/backend"
migrate "quotes"   "$Q/src/QuotesApi.Persistence" "$Q/src/QuotesApi.Host" AppDbContext quotes DefaultConnection

ok "schemas applied"

cleanup_firewall
trap - EXIT
ok "firewall rule removed"

# ---------------------------------------------------------------------------------------------------
step "Waking it up"
#
# Every app scales to zero and both databases auto-pause, so the first request of the day pays a
# container cold start and a database resume back to back. Doing it here means the URLs handed over
# below are warm rather than looking broken.

for pair in "$API_URL/health|quotes-api" "$BFF_URL/healthz|quotes-bff" "$DISPATCH_URL/health|dispatch-api" "$WEB_URL/|quotes-web"; do
  url="${pair%%|*}"; name="${pair##*|}"
  code="$(wait_for_http "$url" 200 40 || true)"
  if [ "$code" = "200" ]; then ok "$name  200"; else warn "$name  $code  (retry in a minute — cold start)"; fi
done

step "Live"
cat <<SUMMARY

    Quotes — the full stack, Day 27 backend behind the Day 22 broker and client
      web        $WEB_URL
      broker     $BFF_URL
      api        $API_URL     (refuses direct calls, by design)

    Dispatch — the capstone, deployed for the first time
      api        $DISPATCH_URL

    SQL          $SQL_FQDN   (databases: quotes, dispatch — Entra-only, no password)
    Key Vault    $VAULT_NAME
    Identity     $IDENTITY_NAME

    Next:  bash scripts/demo.sh
    Stop:  bash scripts/teardown.sh

SUMMARY
