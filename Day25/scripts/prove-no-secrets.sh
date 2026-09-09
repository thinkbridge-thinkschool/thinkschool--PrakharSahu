#!/usr/bin/env bash
#
# Day 25 — prove there are zero secrets in app settings.
#
#   ./scripts/prove-no-secrets.sh
#
# Read-only. Changes nothing.
#
# ---------------------------------------------------------------------------------------------
# WHAT "PROVE" HAS TO MEAN HERE
#
# Grepping app settings for the word "password" proves nothing. A real proof has to do two
# things a grep does not:
#
#   1. Classify EVERY setting, not just look for suspicious ones. An unclassified value is an
#      unreviewed value, and "we found nothing" is only meaningful if everything was examined.
#
#   2. Check the platform state that makes a secret USELESS even if one leaked — Entra-only SQL,
#      local auth disabled on Service Bus, RBAC on the vault, no credentials on the app
#      registration. Absence of a secret in a config blade is weak; inability to use one is
#      strong.
#
# This script does both, and it is deliberately willing to fail.
# ---------------------------------------------------------------------------------------------
set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.." || exit 1

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-identity-dev}"

# ---------------------------------------------------------------------------------------------
# Temp files go in a RELATIVE directory, not /tmp, and that is not a style preference.
#
# This runs under Git Bash on Windows, where bash and python disagree about what /tmp means.
# Bash resolves it inside the MSYS root; the Windows python interpreter resolves it to a
# tmp directory on the C: drive, which does not exist. So a file bash writes to /tmp/x.json
# is genuinely there, and json.load(open("/tmp/x.json")) raises FileNotFoundError.
#
# The first version of this script hit exactly that: section 1 crashed, and section 3 caught the
# same exception and reported "indeterminate", quietly taking a fallback path while looking like
# it had run. A relative path is interpreted identically by both, because both start from the
# same working directory.
# ---------------------------------------------------------------------------------------------
TMP=".proof-tmp"
mkdir -p "$TMP"
trap 'rm -rf "$TMP"' EXIT

PASS=0; FAIL=0
ok()  { printf '  PASS  %s\n' "$1"; PASS=$((PASS+1)); }
bad() { printf '  FAIL  %s\n' "$1"; FAIL=$((FAIL+1)); }

SITE_NAME="$(az webapp list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null)"
[ -n "$SITE_NAME" ] || { echo "No web app in $RESOURCE_GROUP."; exit 1; }

SQL_SERVER="$(az sql server list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null)"
SB_NS="$(az servicebus namespace list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null)"
VAULT="$(az keyvault list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null)"

echo "=============================================================================="
echo " Zero-secrets proof — ${SITE_NAME}"
echo "=============================================================================="

# ---------------------------------------------------------------------------------------------
# 1. EVERY app setting, classified.
#
# The classifier is intentionally strict: anything it does not recognise is reported as UNKNOWN
# and counted as a failure. A value nobody has categorised is exactly where a secret would hide,
# so the default has to be suspicion rather than silence.
# ---------------------------------------------------------------------------------------------
echo
echo "--- 1. Every app setting, classified ------------------------------------------"

az webapp config appsettings list -n "$SITE_NAME" -g "$RESOURCE_GROUP" -o json 2>/dev/null > "$TMP"/appsettings.json

python - <<'PY' > "$TMP"/classify.txt 2>&1
import io, json, re, sys

settings = json.load(io.open('.proof-tmp/appsettings.json', encoding='utf-8'))

# Patterns that indicate an actual credential embedded in a value.
SECRET_PATTERNS = [
    (r'(?i)\bpassword\s*=\s*\S', 'contains Password='),
    (r'(?i)\bpwd\s*=\s*\S', 'contains Pwd='),
    (r'(?i)\baccountkey\s*=\s*\S', 'contains AccountKey='),
    (r'(?i)\bsharedaccesskey\s*=\s*\S', 'contains SharedAccessKey='),
    (r'(?i)sharedaccesssignature', 'contains a SAS token'),
    (r'(?i)\bclient_?secret\s*=\s*\S', 'contains a client secret'),
    (r'(?i)^bearer\s+ey', 'looks like a bearer token'),
    (r'ey[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.', 'looks like a JWT'),
    (r'-----BEGIN [A-Z ]*PRIVATE KEY-----', 'is a private key'),
]

GUID = re.compile(r'^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$')

rows, verdicts = [], []

for s in sorted(settings, key=lambda x: x['name']):
    name, value = s['name'], s.get('value') or ''
    kind, detail, verdict = None, '', 'OK'

    # -- an explicit Key Vault reference: a pointer, never a value -------------------------
    if value.startswith('@Microsoft.KeyVault('):
        kind = 'KEY VAULT REF'
        detail = value
    # -- a GUID: a client id or tenant id. Public by design. -------------------------------
    elif GUID.match(value.strip()):
        kind = 'IDENTIFIER'
        detail = value
    # -- App Insights ingestion. Key-shaped, and honestly labelled. ------------------------
    elif name == 'APPLICATIONINSIGHTS_CONNECTION_STRING':
        kind = 'TELEMETRY INGEST'
        detail = 'InstrumentationKey=<guid>;...  write-only ingestion id; see EXERCISE.md gap 1'
        verdict = 'NOTED'
    # -- a connection string: safe only if it names an auth METHOD and carries no credential
    elif '=' in value and (';' in value or value.lower().startswith('server=')):
        if re.search(r'(?i)authentication\s*=\s*active directory', value):
            kind = 'CONN STR (no credential)'
            detail = value
        else:
            kind = 'CONN STR'
            detail = value
            verdict = 'SUSPECT'
    # -- a hostname or a short plain token -------------------------------------------------
    elif re.match(r'^[A-Za-z0-9._-]+$', value):
        kind = 'ENDPOINT/NAME'
        detail = value
    else:
        kind = 'UNKNOWN'
        detail = value[:60]
        verdict = 'UNKNOWN'

    # Any secret pattern overrides every classification above.
    for pattern, why in SECRET_PATTERNS:
        if re.search(pattern, value):
            kind, detail, verdict = 'SECRET', why, 'SECRET'
            break

    rows.append((name, kind, verdict, detail))
    verdicts.append(verdict)

width = max(len(r[0]) for r in rows)
for name, kind, verdict, detail in rows:
    mark = {'OK': '  ', 'NOTED': ' *', 'SUSPECT': ' ?', 'UNKNOWN': ' ?', 'SECRET': ' !'}[verdict]
    print(f'{mark} {name:<{width}}  [{kind}]')
    if detail and kind in ('KEY VAULT REF', 'CONN STR (no credential)', 'CONN STR', 'TELEMETRY INGEST', 'UNKNOWN', 'SECRET'):
        print(f'   {" " * width}   {detail}')

print('---')
print(f'total={len(rows)}')
print(f'secrets={verdicts.count("SECRET")}')
print(f'suspect={verdicts.count("SUSPECT")}')
print(f'unknown={verdicts.count("UNKNOWN")}')
print(f'noted={verdicts.count("NOTED")}')
PY

sed -n '1,/^---$/p' "$TMP"/classify.txt | sed '$d' | sed 's/^/  /'

TOTAL="$(grep -m1 '^total=' "$TMP"/classify.txt | cut -d= -f2)"
SECRETS="$(grep -m1 '^secrets=' "$TMP"/classify.txt | cut -d= -f2)"
SUSPECT="$(grep -m1 '^suspect=' "$TMP"/classify.txt | cut -d= -f2)"
UNKNOWN="$(grep -m1 '^unknown=' "$TMP"/classify.txt | cut -d= -f2)"

echo
echo "  ${TOTAL} settings examined; * = noted, ? = needs a look, ! = secret"
[ "$SECRETS" = "0" ] && ok "no app setting contains a credential" || bad "$SECRETS setting(s) contain a credential"
[ "$SUSPECT" = "0" ] && ok "no connection string carries a credential" || bad "$SUSPECT connection string(s) look credential-bearing"
[ "$UNKNOWN" = "0" ] && ok "every setting was classified" || bad "$UNKNOWN setting(s) could not be classified"

# ---------------------------------------------------------------------------------------------
# 2. The slot purpose-built for secrets, and whether anything is in it.
# ---------------------------------------------------------------------------------------------
echo
echo "--- 2. The connection strings slot --------------------------------------------"
CS_COUNT="$(az webapp config connection-string list -n "$SITE_NAME" -g "$RESOURCE_GROUP" --query "length(@)" -o tsv 2>/dev/null || echo 0)"
echo "  entries: ${CS_COUNT}"
[ "$CS_COUNT" = "0" ] && ok "the App Service connection-strings slot is empty" \
                      || bad "$CS_COUNT connection string(s) are stored in the dedicated slot"

# ---------------------------------------------------------------------------------------------
# 3. Did the Key Vault reference actually RESOLVE?
#
# This is the check that separates a working configuration from one that only looks right. When
# a reference fails, App Service leaves the literal '@Microsoft.KeyVault(...)' string as the
# value and the app starts anyway. Its own API reports the resolution status, so ask it.
# ---------------------------------------------------------------------------------------------
echo
echo "--- 3. Key Vault reference resolution -----------------------------------------"
SITE_ID="$(az webapp show -n "$SITE_NAME" -g "$RESOURCE_GROUP" --query id -o tsv)"

# App Service publishes the resolution status of every Key Vault reference, and this is the
# authoritative answer — the platform's own view rather than an inference.
#
# It is a GET. The POST form of this path returns an empty body, and the first version of this
# script used POST, caught the resulting parse failure, printed "indeterminate" and quietly fell
# through to a weaker check. It looked exactly like it had run. That is the same class of bug as
# a green what-if that skipped a third of the plan, so it is fixed rather than tolerated.
az rest --method GET \
  --uri "https://management.azure.com${SITE_ID}/config/configreferences/appsettings?api-version=2023-12-01" \
  -o json 2>/dev/null > "$TMP"/refs.json || echo '{}' > "$TMP"/refs.json

python - <<'PYREF' > "$TMP"/refstatus.txt 2>&1
import io, json
try:
    d = json.load(io.open('.proof-tmp/refs.json', encoding='utf-8'))
except Exception:
    d = {}

items = (d or {}).get('value') or []
if not items:
    print('indeterminate')
else:
    resolved = failed = 0
    for item in items:
        pr = item.get('properties') or {}
        status = str(pr.get('status', '?'))
        print("  " + str(item.get('name')))
        print("    status      : " + status)
        print("    detail      : " + str(pr.get('details')))
        print("    resolved by : " + str(pr.get('identityType')) + " identity")
        print("    vault/secret: " + str(pr.get('vaultName')) + " / " + str(pr.get('secretName')))
        if status.lower() == 'resolved':
            resolved += 1
        else:
            failed += 1
    print('resolved=' + str(resolved))
    print('failed=' + str(failed))
PYREF

if grep -q '^indeterminate$' "$TMP"/refstatus.txt; then
  echo "  the resolution API returned nothing; falling back to the app's own report"
  # The app distinguishes a resolved value from the raw reference string, so it can answer this
  # even when the management API will not.
  HOST="$(az webapp show -n "$SITE_NAME" -g "$RESOURCE_GROUP" --query defaultHostName -o tsv)"
  APP_ID="$(az webapp config appsettings list -n "$SITE_NAME" -g "$RESOURCE_GROUP" \
    --query "[?name=='Entra__ClientId'].value | [0]" -o tsv 2>/dev/null)"
  TOKEN="$(az account get-access-token --resource "api://${APP_ID}" --query accessToken -o tsv 2>/dev/null)"
  BODY="$(curl -s --max-time 60 -H "Authorization: Bearer ${TOKEN}" "https://${HOST}/probe/keyvault" 2>/dev/null)"
  if printf '%s' "$BODY" | grep -q '"resolved": *true'; then
    ok "the app reports the Key Vault reference resolved to a real value"
  else
    bad "the Key Vault reference did not resolve"
  fi
else
  grep -v '^resolved=\|^failed=' "$TMP"/refstatus.txt | sed 's/^/  /'
  R="$(grep -m1 '^resolved=' "$TMP"/refstatus.txt | cut -d= -f2)"
  F="$(grep -m1 '^failed=' "$TMP"/refstatus.txt | cut -d= -f2)"
  [ "${F:-1}" = "0" ] && ok "all ${R} Key Vault reference(s) resolved" \
                      || bad "${F} Key Vault reference(s) failed to resolve"
fi

# ---------------------------------------------------------------------------------------------
# 4. Why a leaked secret would be worthless anyway.
#
# The strongest part of the proof. Everything above is about absence; this is about capability.
# ---------------------------------------------------------------------------------------------
echo
echo "--- 4. Password auth is not merely unused, it is refused -----------------------"

AAD_ONLY="$(az rest --method GET \
  --uri "https://management.azure.com/subscriptions/$(az account show --query id -o tsv)/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.Sql/servers/${SQL_SERVER}/azureADOnlyAuthentications/Default?api-version=2023-08-01-preview" \
  --query "properties.azureADOnlyAuthentication" -o tsv 2>/dev/null)"
echo "  SQL azureADOnlyAuthentication : ${AAD_ONLY:-unknown}"
[ "$AAD_ONLY" = "true" ] && ok "SQL refuses password authentication outright" \
                         || bad "SQL still permits SQL-login authentication"

SB_LOCAL="$(az servicebus namespace show -n "$SB_NS" -g "$RESOURCE_GROUP" --query disableLocalAuth -o tsv 2>/dev/null)"
echo "  Service Bus disableLocalAuth  : ${SB_LOCAL:-unknown}"
[ "$SB_LOCAL" = "true" ] && ok "Service Bus rejects its own SAS keys" \
                         || bad "Service Bus still accepts SAS keys"

KV_RBAC="$(az keyvault show -n "$VAULT" -g "$RESOURCE_GROUP" --query "properties.enableRbacAuthorization" -o tsv 2>/dev/null)"
echo "  Key Vault RBAC authorization  : ${KV_RBAC:-unknown}"
[ "$KV_RBAC" = "true" ] && ok "vault access is RBAC, so it is auditable subscription-wide" \
                        || bad "vault uses legacy access policies"

# ---------------------------------------------------------------------------------------------
# 5. The app registration holds no credential of any kind.
# ---------------------------------------------------------------------------------------------
echo
echo "--- 5. The Entra app registration has no credentials ---------------------------"
ENTRA_CLIENT_ID="$(az webapp config appsettings list -n "$SITE_NAME" -g "$RESOURCE_GROUP" \
  --query "[?name=='Entra__ClientId'].value | [0]" -o tsv 2>/dev/null)"

if [ -n "$ENTRA_CLIENT_ID" ]; then
  CREDS="$(az ad app show --id "$ENTRA_CLIENT_ID" \
    --query "{pw:length(passwordCredentials), key:length(keyCredentials)}" -o tsv 2>/dev/null)"
  PW="$(printf '%s' "$CREDS" | cut -f1)"; KEYC="$(printf '%s' "$CREDS" | cut -f2)"
  echo "  passwordCredentials (client secrets): ${PW:-?}"
  echo "  keyCredentials (certificates)       : ${KEYC:-?}"
  { [ "$PW" = "0" ] && [ "$KEYC" = "0" ]; } \
    && ok "the app registration has no client secret and no certificate" \
    || bad "the app registration holds a credential"
else
  bad "could not determine the Entra client id"
fi

# Easy Auth must not name a client-secret setting. Its presence would mean a login flow, which
# would mean a secret.
AUTH="$(az rest --method GET \
  --uri "https://management.azure.com${SITE_ID}/config/authsettingsV2/list?api-version=2023-12-01" \
  -o json 2>/dev/null)"
SECRET_SETTING="$(printf '%s' "$AUTH" | python -c "
import json,sys
try:
    d=json.load(sys.stdin)['properties']['identityProviders']['azureActiveDirectory']['registration']
    print(d.get('clientSecretSettingName') or 'none')
except Exception:
    print('unknown')
" 2>/dev/null)"
UNAUTH_ACTION="$(printf '%s' "$AUTH" | python -c "
import json,sys
try: print(json.load(sys.stdin)['properties']['globalValidation'].get('unauthenticatedClientAction','?'))
except Exception: print('unknown')
" 2>/dev/null)"
echo "  Easy Auth clientSecretSettingName   : ${SECRET_SETTING}"
echo "  Easy Auth unauthenticatedClientAction: ${UNAUTH_ACTION}"
[ "$SECRET_SETTING" = "none" ] && ok "Easy Auth validates tokens without a client secret" \
                               || bad "Easy Auth references a client secret setting"

# ---------------------------------------------------------------------------------------------
# 6. The identity's roles are the narrow ones.
# ---------------------------------------------------------------------------------------------
echo
echo "--- 6. Least privilege on the managed identity ---------------------------------"
MI_PRINCIPAL="$(az identity list -g "$RESOURCE_GROUP" --query "[0].principalId" -o tsv 2>/dev/null)"
ROLES="$(az role assignment list --assignee "$MI_PRINCIPAL" --all \
  --query "[].roleDefinitionName" -o tsv 2>/dev/null | sort)"
printf '%s\n' "$ROLES" | sed 's/^/    /'

BROAD="$(printf '%s\n' "$ROLES" | grep -cE '^(Owner|Contributor|Key Vault Administrator|Azure Service Bus Data Owner)$' || true)"
[ "${BROAD:-0}" = "0" ] && ok "no broad or administrative role is assigned to the identity" \
                        || bad "the identity holds ${BROAD} over-scoped role(s)"

# ---------------------------------------------------------------------------------------------
# 7. No secure parameters were ever passed to a deployment.
# ---------------------------------------------------------------------------------------------
echo
echo "--- 7. No secret ever passed through ARM ---------------------------------------"
SECURE_COUNT="$(az deployment group list -g "$RESOURCE_GROUP" -o json 2>/dev/null | python -c "
import json,sys
n=0
for d in json.load(sys.stdin):
    for k,v in ((d.get('properties') or {}).get('parameters') or {}).items():
        if isinstance(v,dict) and str(v.get('type','')).lower()=='securestring':
            n+=1
print(n)
" 2>/dev/null || echo '?')"
echo "  secureString parameters across all deployments in this group: ${SECURE_COUNT}"
[ "$SECURE_COUNT" = "0" ] && ok "no secret was passed to a deployment, so none is in its history" \
                          || bad "${SECURE_COUNT} secureString parameter(s) appear in deployment history"

KV_RESOURCES="$(az resource list -g "$RESOURCE_GROUP" --resource-type "Microsoft.KeyVault/vaults" --query "length(@)" -o tsv 2>/dev/null)"
echo "  Key Vaults in the group: ${KV_RESOURCES} (holding 1 unavoidable third-party credential)"

echo
echo "=============================================================================="
printf ' %d passed, %d failed\n' "$PASS" "$FAIL"
echo "=============================================================================="
[ "$FAIL" -eq 0 ]
