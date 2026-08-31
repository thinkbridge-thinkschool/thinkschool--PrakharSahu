#!/usr/bin/env bash
#
# Day 17 — verification. Proves the three claims the exercise asks to be defended:
#
#   1. the live URL loads
#   2. the call to the Week-1 API carries a managed-identity token
#   3. no secret exists in the repo or in app settings that could substitute for it
#
# Every check below hits the deployed system. Nothing is asserted from reading source.
#
# Usage:  ./Day17/scripts/verify.sh  [> verification-run.txt]

set -uo pipefail   # deliberately NOT -e: a failing check must be reported, not fatal.

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DAY17_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$DAY17_DIR/.." && pwd)"

# Same pairing as in deploy.sh: MSYS_NO_PATHCONV above keeps Git Bash from mangling Azure
# resource ids, and this puts real filesystem paths back into a form native Windows processes
# can open. No-op on Linux and macOS.
winpath() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

# Scratch directory for response bodies, beside the repo rather than in /tmp.
#
# On Windows the `curl` on PATH is the native curl.exe, not an MSYS build, so `-o /tmp/x.json`
# writes to C:\tmp\x.json while bash and Node read MSYS /tmp — a completely different
# directory. Every check that saved a body and then read it back failed with "No such file",
# on a file curl had just written successfully. So curl is handed a converted path and
# everything else uses the MSYS one.
WORK="$DAY17_DIR/.verify-tmp"
mkdir -p "$WORK"

# fetch <basename> <curl args…> — writes the body to $WORK/<basename>, prints the status code.
fetch() {
  local name="$1"; shift
  curl -s -o "$(winpath "$WORK/$name")" -w '%{http_code}' "$@"
}

OUTPUT="$DAY17_DIR/.deploy-output.json"
[ -f "$OUTPUT" ] || { echo "No $OUTPUT — run deploy.sh first."; exit 1; }

# Every JSON read in this script goes through stdin rather than letting Node open the file.
# Under Git Bash a path like /tmp/x.json or /d/ThinkBridge/... is an MSYS path; Node is a
# native Windows process and resolves it against the current drive root instead (C:\tmp\…),
# so `require()` fails with ENOENT on a file that plainly exists. Piping sidesteps the whole
# translation problem and works identically on Linux in CI.
read_json() {
  node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{console.log(JSON.parse(s)['$1']??'')}catch{console.log('')}})" < "$OUTPUT"
}

# Pretty-prints JSON arriving on stdin, indented for the log; falls back to raw text.
pretty() {
  node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{console.log(JSON.stringify(JSON.parse(s),null,2).replace(/^/gm,"      "))}catch{console.log("      "+s.trim())}})'
}

# json_field <file> <top-level-key>. Same stdin trick, same reason.
json_field() {
  node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{const o=JSON.parse(s);console.log(o['$2']??'')}catch{console.log('')}})" < "$1"
}

# Length of a JSON array on stdin, or a word explaining why it is not one.
json_len() {
  node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{const a=JSON.parse(s);console.log(Array.isArray(a)?a.length:"not-an-array")}catch{console.log("unreadable")}})' < "$1"
}

SWA_URL="$(read_json swaUrl)"
BFF_URL="$(read_json bffUrl)"
API_URL="$(read_json apiUrl)"
APP_ID_URI="$(read_json apiAppIdUri)"
APP_ROLE="$(read_json appRole)"
RESOURCE_GROUP="${RESOURCE_GROUP:-$(read_json resourceGroup)}"
API_APP_NAME="${API_APP_NAME:-quotes-api}"
BFF_APP_NAME="${BFF_APP_NAME:-quotes-bff}"

PASS=0; FAIL=0
section() { printf '\n\n================================================================\n %s\n================================================================\n' "$*"; }
step()    { printf '\n--- %s\n' "$*"; }
ok()      { PASS=$((PASS+1)); printf '  [PASS] %s\n' "$*"; }
no()      { FAIL=$((FAIL+1)); printf '  [FAIL] %s\n' "$*"; }

FRONTEND_MODE="$(read_json frontendMode)"

printf 'Day 17 verification — %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
if [ "$FRONTEND_MODE" = "swa" ]; then
  printf 'frontend : %s   (Azure Static Web Apps, Free)\n' "$SWA_URL"
else
  printf 'frontend : %s\n' "$SWA_URL"
  printf '           NOT Azure Static Web Apps. SWA is offered only in centralus, eastus2,\n'
  printf '           westus2, westeurope and eastasia, and this subscription'"'"'s\n'
  printf '           sys.regionrestriction policy permits none of them. The bundle is served\n'
  printf '           by nginx on Container Apps with a hand-translated copy of\n'
  printf '           staticwebapp.config.json, so the checks below measure the same rules.\n'
fi
printf 'bff      : %s\n' "$BFF_URL"
printf 'api      : %s\n' "$API_URL"

# =========================================================================================
section "1. The live URL loads"

step "GET $SWA_URL"
HOME_STATUS="$(fetch swa-home.html "$SWA_URL")"
printf '  HTTP %s\n' "$HOME_STATUS"
[ "$HOME_STATUS" = "200" ] && ok "Static Web App responds 200" || no "expected 200, got $HOME_STATUS"
grep -q "<app-root>" $WORK/swa-home.html \
  && ok "index.html contains the Angular root element" \
  || no "served document is not the Angular shell"

step "Deep link falls back to index.html (client-side routing)"
DEEP_STATUS="$(curl -s -o /dev/null -w '%{http_code}' "$SWA_URL/quotes/1")"
[ "$DEEP_STATUS" = "200" ] && ok "GET /quotes/1 -> 200 (navigationFallback works)" \
  || no "GET /quotes/1 -> $DEEP_STATUS; a cold deep link would 404"

step "Security headers"
HEADERS="$(curl -sI "$SWA_URL")"
for header in content-security-policy strict-transport-security x-content-type-options referrer-policy; do
  grep -qi "^${header}:" <<<"$HEADERS" && ok "$header present" || no "$header missing"
done
grep -i '^content-security-policy:' <<<"$HEADERS" | sed 's/^/      /'

# =========================================================================================
section "2. The API call carries a managed-identity token"

step "The token the BFF is presenting — GET $BFF_URL/bff/identity"
IDENTITY="$(curl -s "$BFF_URL/bff/identity")"
echo "$IDENTITY" | pretty

grep -q '"source": *"ManagedIdentityCredential' <<<"$IDENTITY" \
  && ok "token came from IMDS (ManagedIdentityCredential), not a secret" \
  || no "token did not come from a managed identity"
grep -q "\"roles\": *\[[^]]*\"$APP_ROLE\"" <<<"$IDENTITY" \
  && ok "token carries the $APP_ROLE app role" \
  || no "token is missing the $APP_ROLE app role"
grep -q '"subjectIsUser": *false' <<<"$IDENTITY" \
  && ok "app-only token — no user in the loop" \
  || no "token appears to be delegated, not app-only"

step "What the API says it authenticated — GET $BFF_URL/api/whoami"
WHOAMI="$(curl -s "$BFF_URL/api/whoami")"
echo "$WHOAMI" | pretty

grep -q '"identityType": *"app"' <<<"$WHOAMI" \
  && ok "API confirms the caller is an application identity" \
  || no "API did not report an application identity"
grep -q "\"$APP_ROLE\"" <<<"$WHOAMI" \
  && ok "API validated the $APP_ROLE role on the incoming token" \
  || no "API did not see the $APP_ROLE role"
grep -q '"userPrincipalName": *null' <<<"$WHOAMI" \
  && ok "no user principal on the caller token (proves it is not a signed-in human)" \
  || no "caller token carries a user principal"

step "NEGATIVE: the same endpoints called directly, with no managed-identity token"
for path in /api/quotes /api/whoami; do
  DIRECT="$(fetch direct.json "$API_URL$path")"
  if [ "$DIRECT" = "401" ]; then
    ok "GET $API_URL$path without X-Caller-Token -> 401"
  else
    no "GET $API_URL$path without X-Caller-Token -> $DIRECT (expected 401)"
  fi
done
sed 's/^/      /' $WORK/direct.json 2>/dev/null; echo

step "NEGATIVE: a forged caller token"
FORGED="$(curl -s -o /dev/null -w '%{http_code}' -H "X-Caller-Token: Bearer not.a.token" "$API_URL/api/quotes")"
[ "$FORGED" = "401" ] && ok "garbage caller token -> 401" || no "garbage caller token -> $FORGED"

# =========================================================================================
section "3. Real Week-1 endpoints, through the deployed path"

step "GET /api/quotes (the list the app's home page renders)"
QUOTES_STATUS="$(fetch quotes.json "$BFF_URL/api/quotes")"
printf '  HTTP %s\n' "$QUOTES_STATUS"
[ "$QUOTES_STATUS" = "200" ] && ok "list endpoint returns 200 through the BFF" \
  || no "expected 200, got $QUOTES_STATUS"
COUNT="$(json_len $WORK/quotes.json)"
printf '  quotes returned: %s\n' "$COUNT"
[ "$COUNT" = "0" ] && printf '  (EMPTY STATE exercised — the UI renders its empty message for this response)\n'
head -c 400 $WORK/quotes.json | sed 's/^/      /'; echo

step "GET /api/quotes/{id} — 404 path (ERROR STATE)"
NOTFOUND="$(curl -s -o /dev/null -w '%{http_code}' "$BFF_URL/api/quotes/99999999")"
[ "$NOTFOUND" = "404" ] && ok "unknown id -> 404" || no "unknown id -> $NOTFOUND (expected 404)"

step "POST /api/auth/login with wrong credentials (FAILED-AUTH STATE)"
BADLOGIN="$(fetch badlogin.json \
  -X POST "$BFF_URL/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"email":"nobody@example.invalid","password":"wrong-password"}')"
[ "$BADLOGIN" = "401" ] && ok "bad credentials -> 401 (and the MI hop still succeeded)" \
  || no "bad credentials -> $BADLOGIN (expected 401)"
sed 's/^/      /' $WORK/badlogin.json 2>/dev/null; echo

step "POST /api/quotes with no user token (401 — user auth is independent of MI auth)"
NOAUTH="$(curl -s -o /dev/null -w '%{http_code}' \
  -X POST "$BFF_URL/api/quotes" \
  -H 'Content-Type: application/json' \
  -d '{"author":"Nobody","text":"This should not be created."}')"
[ "$NOAUTH" = "401" ] && ok "write without a user token -> 401, even though the MI token was valid" \
  || no "write without a user token -> $NOAUTH (expected 401)"

# -----------------------------------------------------------------------------------------
# A full round-trip as a real user, through the broker, against the real Week-1 endpoints.
#
# This is the part that proves the two identities coexist rather than one masking the other:
# every call below carries the managed-identity token AND a user token, and the API has to
# read the right one for each decision it makes.
# -----------------------------------------------------------------------------------------
step "Register a throwaway user — POST /api/auth/register"

# Registering rather than seeding means there is no bootstrap password to generate, store or
# leak. The API signs the new account straight in, so one call yields a usable session.
TEST_EMAIL="day17-verify-$(date +%s)@example.invalid"
REG_STATUS="$(fetch register.json \
  -X POST "$BFF_URL/api/auth/register" \
  -H 'Content-Type: application/json' \
  -d "{\"email\":\"${TEST_EMAIL}\",\"password\":\"Verify-Day17-Passw0rd!\"}")"
printf '  HTTP %s as %s\n' "$REG_STATUS" "$TEST_EMAIL"

TOKEN="$(json_field $WORK/register.json accessToken)"
[ -n "$TOKEN" ] && ok "registration returned a usable access token" \
                || no "registration did not return an access token (HTTP $REG_STATUS)"

if [ -n "$TOKEN" ]; then
  AUTH_HEADER="Authorization: Bearer $TOKEN"

  step "Both identities on one request — GET /api/whoami with a user token"
  BOTH="$(curl -s -H "$AUTH_HEADER" "$BFF_URL/api/whoami")"
  echo "$BOTH" | pretty
  grep -q '"identityType": *"app"' <<<"$BOTH" \
    && ok "caller is still the managed identity" || no "caller identity was lost"
  grep -q "\"email\": *\"$TEST_EMAIL\"" <<<"$BOTH" \
    && ok "end user is the registered human, NOT the managed identity" \
    || no "the API did not report the end user separately"
  grep -q '"quotes.write"' <<<"$BOTH" \
    && ok "user token carries the quotes.write scope" || no "quotes.write scope missing"

  step "POST /api/quotes — create (201)"
  CREATE_STATUS="$(fetch created.json \
    -X POST "$BFF_URL/api/quotes" \
    -H "$AUTH_HEADER" -H 'Content-Type: application/json' \
    -d '{"author":"Ada Lovelace","text":"That brain of mine is something more than merely mortal."}')"
  [ "$CREATE_STATUS" = "201" ] && ok "created -> 201" || no "create -> $CREATE_STATUS (expected 201)"
  sed 's/^/      /' $WORK/created.json; echo
  QUOTE_ID="$(json_field $WORK/created.json id)"

  # The six-field shape the frontend's isQuote() guard demands. A response missing any of
  # them renders as "the Quotes API returned something this app does not understand".
  for field in id text author createdAt isDeleted userId; do
    grep -q "\"$field\"" $WORK/created.json \
      && ok "response carries '$field'" || no "response is missing '$field'"
  done

  step "POST the same author+text again — duplicate (409)"
  DUP="$(curl -s -o /dev/null -w '%{http_code}' \
    -X POST "$BFF_URL/api/quotes" \
    -H "$AUTH_HEADER" -H 'Content-Type: application/json' \
    -d '{"author":"Ada Lovelace","text":"That brain of mine is something more than merely mortal."}')"
  [ "$DUP" = "409" ] && ok "duplicate rejected -> 409" || no "duplicate -> $DUP (expected 409)"

  if [ -n "$QUOTE_ID" ]; then
    step "GET /api/quotes/$QUOTE_ID — read back (200)"
    READ="$(curl -s -o /dev/null -w '%{http_code}' "$BFF_URL/api/quotes/$QUOTE_ID")"
    [ "$READ" = "200" ] && ok "read back -> 200" || no "read back -> $READ"

    step "DELETE /api/quotes/$QUOTE_ID as the owner (204)"
    DEL="$(curl -s -o /dev/null -w '%{http_code}' -X DELETE "$BFF_URL/api/quotes/$QUOTE_ID" -H "$AUTH_HEADER")"
    [ "$DEL" = "204" ] && ok "owner delete -> 204 (ownership resolved against the USER, not the identity)" \
                       || no "owner delete -> $DEL (expected 204)"

    step "GET /api/quotes/$QUOTE_ID after the soft delete (404)"
    GONE="$(curl -s -o /dev/null -w '%{http_code}' "$BFF_URL/api/quotes/$QUOTE_ID")"
    [ "$GONE" = "404" ] && ok "soft-deleted quote -> 404" || no "deleted quote -> $GONE (expected 404)"
  fi
fi

step "CORS preflight from the Static Web App origin"
PREFLIGHT="$(curl -s -D - -o /dev/null -X OPTIONS "$BFF_URL/api/quotes" \
  -H "Origin: $SWA_URL" -H 'Access-Control-Request-Method: GET' \
  -H 'Access-Control-Request-Headers: authorization,content-type')"
grep -qi "access-control-allow-origin: *$SWA_URL" <<<"$PREFLIGHT" \
  && ok "preflight echoes the SWA origin" || no "preflight did not allow the SWA origin"
grep -qi 'access-control-allow-credentials: *true' <<<"$PREFLIGHT" \
  && ok "credentials allowed (needed for the refresh cookie)" || no "credentials not allowed"

step "CORS rejects an origin that is not the Static Web App"
EVIL="$(curl -s -D - -o /dev/null -X OPTIONS "$BFF_URL/api/quotes" \
  -H 'Origin: https://evil.example.com' -H 'Access-Control-Request-Method: GET')"
grep -qi 'access-control-allow-origin' <<<"$EVIL" \
  && no "an arbitrary origin was allowed" || ok "unknown origin gets no CORS grant"

# =========================================================================================
section "4. No secret anywhere in the managed-identity path"

step "Repository scan"
HITS="$(grep -rInE '(client_secret|clientSecret|AZURE_CLIENT_SECRET|SharedAccessKey|AccountKey=|password=[^$])' \
  "$DAY17_DIR" --exclude-dir=node_modules --exclude-dir=dist --exclude-dir=bin --exclude-dir=obj \
  --exclude='*.md' --exclude='verify.sh' --exclude-dir=.verify-tmp 2>/dev/null || true)"
# verify.sh is excluded because it contains the search pattern itself, and a scanner that
# always reports one hit — its own — is a scanner nobody reads.
if [ -z "$HITS" ]; then
  ok "no client secret, account key, or password literal in Day17/"
else
  no "possible secret material found:"; sed 's/^/      /' <<<"$HITS"
fi

step "BFF app settings — every environment variable, in full"
az containerapp show -n "$BFF_APP_NAME" -g "$RESOURCE_GROUP" \
  --query "properties.template.containers[0].env" -o json 2>/dev/null | sed 's/^/      /'
# `length(...)` on a null returns nothing rather than 0, so the empty case is normalised
# here. Written as an if rather than the `[ … ] && ok || no` chain it replaces: that chain
# reports a pass whenever the FIRST test fails but the second succeeds, which is exactly
# backwards, and it printed "unknown secret(s) defined" for an app that had none.
BFF_SECRETS="$(az containerapp show -n "$BFF_APP_NAME" -g "$RESOURCE_GROUP" \
  --query "length(properties.configuration.secrets || \`[]\`)" -o tsv 2>/dev/null || echo "")"
[ -z "$BFF_SECRETS" ] && BFF_SECRETS=0
printf '  Container Apps secrets defined on the BFF: %s\n' "$BFF_SECRETS"
if [ "$BFF_SECRETS" = "0" ]; then
  ok "the BFF holds zero secrets — its only credential is minted at runtime from IMDS"
else
  no "the BFF has $BFF_SECRETS secret(s) defined"
fi

step "The API's own settings, stated honestly"
az containerapp show -n "$API_APP_NAME" -g "$RESOURCE_GROUP" \
  --query "properties.configuration.secrets[].name" -o json 2>/dev/null | sed 's/^/      /'
cat <<'NOTE'
      Read this carefully rather than as a pass/fail.

      The API does hold Container Apps secrets, and they are NOT part of the
      managed-identity path:

        jwt-key        signs the tokens the API issues to its own end users. It is a
                       signing key, not a credential for calling anything. Removing it
                       would not remove a client secret; it would remove the ability to
                       log a human in at all.

      That is the whole list — one entry. No seed account is configured either, because
      the verification below registers a throwaway user through the real
      POST /api/auth/register instead, leaving no bootstrap password to store.

      What the exercise asks about — a client secret used to authenticate the frontend
      tier to the API — does not exist. The BFF authenticates with a token IMDS mints on
      demand. There is no credential to rotate, leak, or check in, which is why the BFF's
      secret count above is zero.
NOTE

step "App registration has no credentials of its own"
APP_ID="$(read_json apiAppId)"
PW_COUNT="$(az ad app show --id "$APP_ID" --query "length(passwordCredentials)" -o tsv 2>/dev/null || echo unknown)"
KEY_COUNT="$(az ad app show --id "$APP_ID" --query "length(keyCredentials)" -o tsv 2>/dev/null || echo unknown)"
printf '  passwordCredentials: %s   keyCredentials: %s\n' "$PW_COUNT" "$KEY_COUNT"
[ "$PW_COUNT" = "0" ] && [ "$KEY_COUNT" = "0" ] \
  && ok "the app registration has no client secret and no certificate" \
  || no "the app registration has credentials attached ($PW_COUNT password, $KEY_COUNT cert)"

# =========================================================================================
section "5. Lighthouse"

step "Running Lighthouse against $SWA_URL"

mkdir -p "$DAY17_DIR/docs"
LH_OUT="$DAY17_DIR/docs/lighthouse"

# --output-path goes to Lighthouse, which is a native Node process, so it needs a real
# Windows path under Git Bash. Output is not piped, so a Lighthouse crash still shows here.
npx --yes lighthouse@latest "$SWA_URL" \
  --quiet --chrome-flags="--headless=new --no-sandbox" \
  --preset=desktop \
  --output=json --output=html \
  --output-path="$(winpath "$LH_OUT")" > /tmp/lighthouse.log 2>&1 \
  || { printf '  lighthouse exited non-zero:\n'; tail -12 /tmp/lighthouse.log | sed 's/^/      /'; }

if [ -f "${LH_OUT}.report.json" ]; then
  # Piped in rather than require()d, for the path reason in read_json above.
  if node -e '
      let s = "";
      process.stdin.on("data", d => s += d).on("end", () => {
        const r = JSON.parse(s);
        const cats = ["performance", "accessibility", "best-practices", "seo"];
        let allPass = true;
        for (const key of cats) {
          const cat = r.categories[key];
          const score = Math.round((cat?.score ?? 0) * 100);
          if (score < 95) allPass = false;
          console.log(`  ${score >= 95 ? "[PASS]" : "[FAIL]"} ${(cat?.title ?? key).padEnd(16)} ${score}`);
        }
        console.log("");
        for (const m of ["first-contentful-paint", "largest-contentful-paint",
                         "total-blocking-time", "cumulative-layout-shift", "speed-index"]) {
          if (r.audits?.[m]) console.log(`         ${r.audits[m].title.padEnd(26)} ${r.audits[m].displayValue}`);
        }
        process.exitCode = allPass ? 0 : 1;
      });
    ' < "${LH_OUT}.report.json"; then
    PASS=$((PASS+1))
  else
    FAIL=$((FAIL+1))
  fi
  printf '\n  Full report: Day17/docs/lighthouse.report.html\n'
else
  no "Lighthouse produced no report — see /tmp/lighthouse.log"
fi

# =========================================================================================
section "Result"
printf '  %d passed, %d failed\n\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ] || exit 1
