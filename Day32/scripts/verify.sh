#!/usr/bin/env bash
#
# Assert every binding in the deployment. Read-only.
#
#   bash scripts/verify.sh
#
# This is not the demo. The demo drives the applications through their behaviour; this checks the
# WIRING — that each thing points at the thing it is supposed to point at, and that the permissions
# which make those pointers work actually exist.
#
# It exists because almost every failure in this deployment was a binding that looked right:
#
#   a SAS key for a namespace that rejects SAS keys
#   a database user created from the wrong one of an identity's three GUIDs
#   a client asking /api/... of an API that serves /api/v1/...
#   a role definition GUID with a plausible first half
#   an operator with Owner on the subscription and no access to the vault's contents
#
# None of those are visible in a template review. All of them are one assertion each.

set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
set +e

PASS=0; FAIL=0
section() { printf '\n%s\n %s\n%s\n' "$(printf '=%.0s' {1..90})" "$*" "$(printf '=%.0s' {1..90})"; }
yes()  { PASS=$((PASS+1)); printf '  %s[pass]%s %s\n' "$GREEN" "$RESET" "$*"; }
no()   { FAIL=$((FAIL+1)); printf '  %s[FAIL]%s %s\n' "$RED" "$RESET" "$*"; }

# assert <description> <expected> <actual>
assert_eq() {
  if [ "$2" = "$3" ]; then yes "$1"; else no "$1 — expected '$2', got '$3'"; fi
}

# az.cmd on Windows emits CRLF. Piping tsv through `tr` to join lines leaves the carriage return
# behind, so two strings that PRINT identically compare unequal — which produced the most
# confusing possible failure message: "expected 'dispatch quotes ', got 'dispatch quotes '".
tsv_list() { tr -d '\r' | sort | tr '\n' ' '; }

# assert_has <description> <needle> <haystack>
assert_has() {
  case "$3" in
    *"$2"*) yes "$1" ;;
    *)      no "$1 — '$2' not found in: ${3:0:120}" ;;
  esac
}
assert_not() {
  case "$3" in
    *"$2"*) no "$1 — '$2' IS present, and must not be" ;;
    *)      yes "$1" ;;
  esac
}

az account set --subscription "$SUBSCRIPTION_ID" 2>/dev/null

ENV_DOMAIN="$(env_default_domain)"
WEB="$(app_url "$QUOTES_WEB_APP" "$ENV_DOMAIN")"
BFF="$(app_url "$QUOTES_BFF_APP" "$ENV_DOMAIN")"
API="$(app_url "$QUOTES_API_APP" "$ENV_DOMAIN")"
DISPATCH="$(app_url "$DISPATCH_API_APP" "$ENV_DOMAIN")"

env_of() {
  az containerapp show -n "$1" -g "$RESOURCE_GROUP" \
    --query "properties.template.containers[0].env[?name=='$2'].value | [0]" -o tsv 2>/dev/null
}
secret_ref_of() {
  az containerapp show -n "$1" -g "$RESOURCE_GROUP" \
    --query "properties.template.containers[0].env[?name=='$2'].secretRef | [0]" -o tsv 2>/dev/null
}

# =================================================================================================
section "1. The resource group exists and holds what it should"

for kind in "Microsoft.ManagedIdentity/userAssignedIdentities" "Microsoft.KeyVault/vaults" \
            "Microsoft.Sql/servers" "Microsoft.App/containerApps"; do
  COUNT="$(az resource list -g "$RESOURCE_GROUP" --resource-type "$kind" --query "length(@)" -o tsv 2>/dev/null)"
  case "$kind" in
    *containerApps) assert_eq "4 container apps" "4" "${COUNT:-0}" ;;
    *)              [ "${COUNT:-0}" -ge 1 ] && yes "${kind##*/} present" || no "${kind##*/} missing" ;;
  esac
done

IDENTITY_NAME="$(az identity list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null)"
CLIENT_ID="$(az identity show -n "$IDENTITY_NAME" -g "$RESOURCE_GROUP" --query clientId -o tsv 2>/dev/null)"
PRINCIPAL_ID="$(az identity show -n "$IDENTITY_NAME" -g "$RESOURCE_GROUP" --query principalId -o tsv 2>/dev/null)"
VAULT="$(az keyvault list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null)"
SQL_SERVER="$(az sql server list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null)"
SQL_FQDN="$(az sql server show -n "$SQL_SERVER" -g "$RESOURCE_GROUP" --query fullyQualifiedDomainName -o tsv 2>/dev/null)"

printf '\n  identity %s\n  clientId %s\n  vault    %s\n  sql      %s\n' \
  "$IDENTITY_NAME" "$CLIENT_ID" "$VAULT" "$SQL_FQDN"

[ -n "$CLIENT_ID" ] && [ "$CLIENT_ID" != "$PRINCIPAL_ID" ] \
  && yes "clientId and principalId are different values (they are not interchangeable)" \
  || no "could not read both identity ids"

# =================================================================================================
section "2. Permissions — the part that is invisible until something 403s"

ACR_ID="$(az acr show -n "$ACR_NAME" --query id -o tsv 2>/dev/null)"
ACR_ROLES="$(az role assignment list --scope "$ACR_ID" --assignee "$PRINCIPAL_ID" --query "[].roleDefinitionName" -o tsv 2>/dev/null)"
assert_has "identity holds AcrPull on $ACR_NAME" "AcrPull" "$ACR_ROLES"

SB_ID="$(az servicebus namespace show -n "$SERVICEBUS_NAMESPACE" -g "$SERVICEBUS_RG" --query id -o tsv 2>/dev/null)"
SB_ROLES="$(az role assignment list --scope "$SB_ID" --assignee "$PRINCIPAL_ID" --query "[].roleDefinitionName" -o tsv 2>/dev/null)"
assert_has "identity holds Service Bus Data Sender"   "Data Sender"   "$SB_ROLES"
assert_has "identity holds Service Bus Data Receiver" "Data Receiver" "$SB_ROLES"
assert_not "identity does NOT hold Data Owner (send+receive is enough)" "Data Owner" "$SB_ROLES"

VAULT_ID="$(az keyvault show -n "$VAULT" --query id -o tsv 2>/dev/null)"
KV_APP_ROLES="$(az role assignment list --scope "$VAULT_ID" --assignee "$PRINCIPAL_ID" --query "[].roleDefinitionName" -o tsv 2>/dev/null)"
assert_has "identity holds Key Vault Secrets User" "Secrets User" "$KV_APP_ROLES"
assert_not "identity cannot WRITE secrets"         "Secrets Officer" "$KV_APP_ROLES"

OPERATOR_ID="$(az ad signed-in-user show --query id -o tsv 2>/dev/null)"
KV_OP_ROLES="$(az role assignment list --scope "$VAULT_ID" --assignee "$OPERATOR_ID" --query "[].roleDefinitionName" -o tsv 2>/dev/null)"
assert_has "operator holds Key Vault Secrets Officer (so a redeploy reuses the signing key)" \
  "Secrets Officer" "$KV_OP_ROLES"

API_SP="$(az ad sp show --id "$QUOTES_API_APP_ID" --query id -o tsv 2>/dev/null)"
ROLE_ID="$(az ad app show --id "$QUOTES_API_APP_ID" --query "appRoles[?value=='$QUOTES_API_ROLE'].id | [0]" -o tsv 2>/dev/null)"
GRANTED="$(az rest --method GET --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$PRINCIPAL_ID/appRoleAssignments" \
  --query "value[?appRoleId=='$ROLE_ID'] | [0].resourceId" -o tsv 2>/dev/null)"
assert_eq "identity is granted $QUOTES_API_ROLE on the Quotes API app" "$API_SP" "$GRANTED"

# =================================================================================================
section "3. Secrets — there should be exactly one, and no password anywhere"

SECRET_COUNT="$(az keyvault secret list --vault-name "$VAULT" --query "length(@)" -o tsv 2>/dev/null)"
assert_eq "Key Vault holds exactly 1 secret" "1" "${SECRET_COUNT:-0}"
# Bicep does not delete child resources it has stopped declaring. Removing the Service Bus secret
# from the template left the secret sitting in the vault -- a credential that no longer works and
# that nothing would ever have noticed. This assertion is the only reason it was found.
SECRET_NAMES="$(az keyvault secret list --vault-name "$VAULT" --query "[].name" -o tsv 2>/dev/null | tsv_list)"
assert_eq "and it is only the JWT signing key" "jwt-signing-key " "$SECRET_NAMES"

assert_eq "Jwt__Key is a secret REFERENCE, not a value" "jwt-signing-key" "$(secret_ref_of "$QUOTES_API_APP" Jwt__Key)"
assert_eq "Jwt__Key has no inline value" "" "$(env_of "$QUOTES_API_APP" Jwt__Key)"

for app in "$QUOTES_API_APP" "$QUOTES_BFF_APP" "$QUOTES_WEB_APP" "$DISPATCH_API_APP"; do
  ALL_ENV="$(az containerapp show -n "$app" -g "$RESOURCE_GROUP" --query "properties.template.containers[0].env" -o json 2>/dev/null)"
  assert_not "$app carries no Password= " "Password=" "$ALL_ENV"
  assert_not "$app carries no SharedAccessKey" "SharedAccessKey" "$ALL_ENV"
done

# =================================================================================================
section "4. SQL — Entra only, two databases, the identity has a user in both"

# `az sql server show --query administrators.azureADOnlyAuthentication` returns EMPTY rather than
# false: that command does not expand the property. The first version of this check therefore
# reported the server as accepting passwords when it does not. An assertion that cannot tell
# "false" apart from "I could not read it" is worse than no assertion at all.
AAD_ONLY="$(az sql server ad-only-auth get -n "$SQL_SERVER" -g "$RESOURCE_GROUP" \
  --query azureAdOnlyAuthentication -o tsv 2>/dev/null | tr -d '\r' | tr A-Z a-z)"
assert_eq "SQL authentication is disabled (Entra only)" "true" "$AAD_ONLY"

DBS="$(az sql db list -s "$SQL_SERVER" -g "$RESOURCE_GROUP" --query "[?name!='master'].name" -o tsv 2>/dev/null | tsv_list)"
assert_eq "databases are quotes and dispatch" "dispatch quotes " "$DBS"

for db in quotes dispatch; do
  CONN="$(env_of "$([ "$db" = quotes ] && echo "$QUOTES_API_APP" || echo "$DISPATCH_API_APP")" \
      "$([ "$db" = quotes ] && echo ConnectionStrings__DefaultConnection || echo ConnectionStrings__Dispatch)")"
  assert_has "[$db] connection string uses managed identity" "Authentication=Active Directory Managed Identity" "$CONN"
  assert_has "[$db] connection string names the CLIENT id"   "User Id=$CLIENT_ID" "$CONN"
  assert_not "[$db] connection string has no password"       "Password" "$CONN"
  assert_has "[$db] connection string points at this server" "$SQL_FQDN" "$CONN"
done

# =================================================================================================
section "5. Messaging — a namespace, not a credential"

SB_NS="$(env_of "$QUOTES_API_APP" ServiceBus__FullyQualifiedNamespace)"
assert_eq "Quotes API is pointed at Day 26's namespace" "$SERVICEBUS_NAMESPACE.servicebus.windows.net" "$SB_NS"
assert_eq "no ServiceBus__ConnectionString is set at all" "" "$(env_of "$QUOTES_API_APP" ServiceBus__ConnectionString)"
assert_eq "AZURE_CLIENT_ID tells DefaultAzureCredential which identity to use" "$CLIENT_ID" \
  "$(env_of "$QUOTES_API_APP" AZURE_CLIENT_ID)"
# Some az commands render ARM booleans as True and others as true. The casing is not the claim
# being made, so it is normalised rather than asserted.
SAS_DISABLED="$(az servicebus namespace show -n "$SERVICEBUS_NAMESPACE" -g "$SERVICEBUS_RG" \
  --query disableLocalAuth -o tsv 2>/dev/null | tr -d '\r' | tr A-Z a-z)"
assert_eq "the namespace really does reject SAS (which is why the above matters)" "true" "$SAS_DISABLED"

TOPIC_SUBS="$(az servicebus topic subscription list --namespace-name "$SERVICEBUS_NAMESPACE" -g "$SERVICEBUS_RG" \
  --topic-name quote-events --query "[].name" -o tsv 2>/dev/null | tsv_list)"
assert_eq "quote-events has both subscriptions" "audit search-index " "$TOPIC_SUBS"

REDIS_HOST="$(az redis list -g "$RESOURCE_GROUP" --query "[0].hostName" -o tsv 2>/dev/null | tr -d '
')"
if [ -n "$REDIS_HOST" ]; then
  assert_eq "Quotes API is pointed at the cache" "$REDIS_HOST:6380" "$(env_of "$QUOTES_API_APP" ConnectionStrings__Redis)"
  assert_not "the cache endpoint carries no password" "password=" "$(env_of "$QUOTES_API_APP" ConnectionStrings__Redis)"
  REDIS_NAME="$(az redis list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null | tr -d '
')"
  assert_eq "token authentication is enabled on the cache" "True"     "$(az redis show -n "$REDIS_NAME" -g "$RESOURCE_GROUP" --query "redisConfiguration.\"aad-enabled\"" -o tsv 2>/dev/null | tr -d '
')"
  assert_eq "the non-TLS port is closed" "False"     "$(az redis show -n "$REDIS_NAME" -g "$RESOURCE_GROUP" --query enableNonSslPort -o tsv 2>/dev/null | tr -d '
')"
else
  no "no Redis cache found in $RESOURCE_GROUP"
fi

# =================================================================================================
section "6. The three tiers point at each other"

assert_eq "broker's upstream is the Quotes API"        "$API" "$(env_of "$QUOTES_BFF_APP" UPSTREAM_API_BASE)"
assert_eq "broker's allowed origin is the web app"     "$WEB" "$(env_of "$QUOTES_BFF_APP" ALLOWED_ORIGINS)"
assert_eq "broker's audience matches the API's"        "$(env_of "$QUOTES_API_APP" CallerIdentity__Audience)" \
                                                       "$(env_of "$QUOTES_BFF_APP" API_APP_ID_URI)"
assert_eq "broker knows which identity to present"     "$CLIENT_ID" "$(env_of "$QUOTES_BFF_APP" AZURE_CLIENT_ID)"
assert_eq "web app's CSP origin is the broker"         "$BFF" "$(env_of "$QUOTES_WEB_APP" BFF_ORIGIN)"

# The bundle is built, not configured, so this is the one binding a template cannot guarantee.
BUNDLE="$(curl -s --max-time 60 "$WEB/" | grep -oE 'main-[A-Za-z0-9]+\.js' | head -1)"
if [ -n "$BUNDLE" ]; then
  BAKED="$(curl -s --max-time 90 "$WEB/$BUNDLE" | grep -oE 'https://[a-z0-9.-]*azurecontainerapps\.io/api/v[0-9]+' | head -1)"
  assert_eq "the Angular bundle calls the broker at /api/v1" "$BFF/api/v1" "$BAKED"
else
  no "could not find the main bundle in the served index.html"
fi

CSP="$(curl -s -D - -o /dev/null --max-time 60 "$WEB/" | tr -d '\r' | grep -i '^content-security-policy:')"
assert_has "CSP connect-src names the broker" "$QUOTES_BFF_APP" "$CSP"

# =================================================================================================
section "7. Images — all four pinned, none on :latest"

for pair in "$QUOTES_API_APP|quotes-api-day32" "$QUOTES_BFF_APP|quotes-bff-day32" \
            "$QUOTES_WEB_APP|quotes-web-day32" "$DISPATCH_API_APP|dispatch-api"; do
  app="${pair%%|*}"; repo="${pair##*|}"
  IMG="$(az containerapp show -n "$app" -g "$RESOURCE_GROUP" --query "properties.template.containers[0].image" -o tsv 2>/dev/null)"
  assert_eq "$app runs $repo:$IMAGE_TAG" "$ACR_NAME.azurecr.io/$repo:$IMAGE_TAG" "$IMG"
  assert_not "$app is not on :latest" ":latest" "$IMG"
done

# =================================================================================================
section "8. Runtime — every tier answers, and the boundary holds"

for pair in "$API/health|quotes-api" "$BFF/healthz|broker" "$DISPATCH/health|dispatch-api" "$WEB/|web"; do
  url="${pair%%|*}"; name="${pair##*|}"
  CODE="$(wait_for_http "$url" 200 40)"
  assert_eq "$name answers 200" "200" "$CODE"
done

DIRECT="$(curl -s -o /dev/null -w '%{http_code}' --max-time 60 "$API/api/v1/quotes")"
if [ "$DIRECT" = "401" ] || [ "$DIRECT" = "403" ]; then
  yes "the Quotes API refuses a direct call ($DIRECT) while /health answers 200"
else
  no "the Quotes API answered $DIRECT to an unauthenticated direct call"
fi

DISPATCH_APP_ID="$(az ad app list --display-name "$DISPATCH_APP_NAME" --query "[0].appId" -o tsv 2>/dev/null | tr -d '')"
assert_eq "Dispatch requires its OWN audience, not the Quotes API one" "api://$DISPATCH_APP_ID"   "$(env_of "$DISPATCH_API_APP" Auth__Audience)"
assert_not "Dispatch does not accept the Quotes API audience" "$QUOTES_API_APP_ID"   "$(env_of "$DISPATCH_API_APP" Auth__Audience)"
DISPATCH_ANON="$(curl -s -o /dev/null -w '%{http_code}' --max-time 60 "$DISPATCH/api/invoices")"
assert_eq "Dispatch refuses an unauthenticated read" "401" "$DISPATCH_ANON"

assert_eq "Dispatch does not auto-migrate on startup" "false" \
  "$(env_of "$DISPATCH_API_APP" DISPATCH_MIGRATE_ON_STARTUP)"

assert_has "Quotes API reports telemetry to Day 26's component" "InstrumentationKey=" \
  "$(env_of "$QUOTES_API_APP" APPLICATIONINSIGHTS_CONNECTION_STRING)"

# =================================================================================================
section "Summary"
printf '\n  %s passed, %s failed\n\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ]
