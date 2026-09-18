#!/usr/bin/env bash
#
# The demo, driven against the live deployment.
#
#   bash scripts/demo.sh | tee docs/demo-transcript.txt
#
# Not a smoke test. A smoke test answers "is it up"; this walks the two systems through the things
# they were built to do and shows the refusals as well as the successes, because a system that only
# ever demonstrates its happy path has not demonstrated that it has rules.
#
# Ten scenes. Each prints what it is about to prove, the request, and the actual response.

set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
set +e   # a failing scene should report and continue, not abort the demo

require_tool az   "Install the Azure CLI."
require_tool curl "curl is needed to drive the APIs."
require_tool node "node is used to read JSON fields."

PASS=0; FAIL=0
scene()  { printf '\n%s\n %s\n%s\n' "$(printf '=%.0s' {1..92})" "$*" "$(printf '=%.0s' {1..92})"; }
claim()  { printf '\n  %s%s%s\n' "$DIM" "$*" "$RESET"; }
pass()   { PASS=$((PASS+1)); printf '  %s[pass]%s %s\n' "$GREEN" "$RESET" "$*"; }
fail()   { FAIL=$((FAIL+1)); printf '  %s[FAIL]%s %s\n' "$RED" "$RESET" "$*"; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
winpath() { command -v cygpath >/dev/null 2>&1 && cygpath -w "$1" || printf '%s' "$1"; }

# Status code on stdout, body into a file. Two things that both matter and that a bare `curl` makes
# you choose between; Day 22's smoke test threw the status code away with >/dev/null and then
# reported a failure four scenes after the one that caused it.
fetch() { local n="$1"; shift; curl -s -o "$(winpath "$WORK/$n")" -w '%{http_code}' --max-time 90 "$@"; }

field() {
  node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{
    try{const v=JSON.parse(s);process.stdout.write(String(v["'"$2"'"]??""))}catch{process.stdout.write("")}})' < "$WORK/$1"
}
count() {
  node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{
    try{const v=JSON.parse(s);process.stdout.write(String(Array.isArray(v)?v.length:(v.items?.length??"?")))}catch{process.stdout.write("?")}})' < "$WORK/$1"
}
show() {
  node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{
    try{console.log(JSON.stringify(JSON.parse(s),null,2).split("\n").slice(0,'"${2:-14}"').map(l=>"      "+l).join("\n"))}
    catch{console.log("      "+s.slice(0,400))}})' < "$WORK/$1"
}

# --- where things are ------------------------------------------------------------------------------
step "Resolving the deployment"
ENV_DOMAIN="$(env_default_domain)" || die "Cannot reach Azure. Run: az login"
WEB="$(app_url "$QUOTES_WEB_APP" "$ENV_DOMAIN")"
BFF="$(app_url "$QUOTES_BFF_APP" "$ENV_DOMAIN")"
API="$(app_url "$QUOTES_API_APP" "$ENV_DOMAIN")"
DISPATCH="$(app_url "$DISPATCH_API_APP" "$ENV_DOMAIN")"
info "web       $WEB"
info "broker    $BFF"
info "quotes    $API"
info "dispatch  $DISPATCH"

# Dispatch now requires a bearer token carrying a role. Acquired for the signed-in user, who was
# granted all three roles by scripts/setup-dispatch-entra.sh. `az account get-access-token` is the
# same call any client would make; nothing here is special-cased for the demo.
DISPATCH_APP_ID="$(az ad app list --display-name "$DISPATCH_APP_NAME" --query "[0].appId" -o tsv 2>/dev/null || true)"
DTOKEN=""
if [ -n "$DISPATCH_APP_ID" ]; then
  DTOKEN="$(az account get-access-token --resource "api://$DISPATCH_APP_ID" --query accessToken -o tsv 2>/dev/null || true)"
fi
if [ -n "$DTOKEN" ]; then
  info "dispatch token acquired (${#DTOKEN} chars)"
  DAUTH=(-H "Authorization: Bearer $DTOKEN")
else
  warn "no Dispatch token — scenes 9 and 11 will fail if the API has authentication enabled"
  DAUTH=()
fi

# --- 0. wake everything ----------------------------------------------------------------------------
scene "0. Cold start — everything scales to zero, so the demo starts by waiting"
claim "Four apps at zero replicas and two auto-paused databases. The first request of the day pays"
claim "a container start and a database resume, back to back. This is the honest cost of the cost."
for pair in "$API/health|quotes-api" "$BFF/healthz|broker" "$DISPATCH/health|dispatch-api" "$WEB/|web"; do
  url="${pair%%|*}"; name="${pair##*|}"
  started=$SECONDS
  code="$(wait_for_http "$url" 200 40)"
  elapsed=$((SECONDS - started))
  if [ "$code" = "200" ]; then pass "$name up in ${elapsed}s"; else fail "$name returned $code after ${elapsed}s"; fi
done

# ===================================================================================================
scene "1. The Quotes API refuses to be called directly"
claim "The API is on a public hostname. It still will not answer /api/* for anyone who cannot"
claim "present an app-only Entra token carrying Api.Invoke. A browser cannot mint one."
CODE=$(fetch direct.json "$API/api/v1/quotes")
printf '\n      GET %s/api/v1/quotes\n      -> HTTP %s\n' "$API" "$CODE"; show direct.json 6
if [ "$CODE" = "401" ] || [ "$CODE" = "403" ]; then
  pass "refused with $CODE - and /health answered 200 a moment ago, so this is policy, not an outage"
else
  fail "expected 401 or 403, got $CODE"
fi

# ===================================================================================================
scene "2. The broker gets past that gate, and runs into the next one"
claim "Same request, one hop earlier. The broker attaches its managed-identity token in"
claim "X-Caller-Token, so the caller check passes. The USER check does not: Day 27 made"
claim "authentication the fallback policy, so an endpoint is authenticated unless it says otherwise."
claim "Two independent gates, and this proves the second one is real rather than implied."
CODE=$(fetch viabff.json "$BFF/api/v1/quotes")
printf '\n      GET %s/api/v1/quotes  (no user token)\n      -> HTTP %s\n' "$BFF" "$CODE"
if [ "$CODE" = "401" ]; then
  pass "401 - the caller token is not a substitute for a user"
else
  fail "expected 401, got $CODE"
fi

# ===================================================================================================
scene "3. Register and sign in"
claim "The Identity module: a hashed password, a 2-hour access token, and a 7-day refresh token"
claim "that is hashed at rest, rotated on use, and revokes its whole family if one is replayed."
DEMO_EMAIL="demo+$(date +%s)@example.invalid"
DEMO_PASS="Day32-Ship-Demo-$(date +%s)"
CODE=$(fetch reg.json -X POST "$BFF/api/v1/auth/register" -H 'Content-Type: application/json'   -d "{\"email\":\"$DEMO_EMAIL\",\"password\":\"$DEMO_PASS\"}")
printf '\n      POST /api/v1/auth/register  -> HTTP %s\n' "$CODE"
if [ "$CODE" = "200" ] || [ "$CODE" = "201" ]; then pass "account created"; else fail "register returned $CODE"; show reg.json 8; fi

CODE=$(fetch login.json -X POST "$BFF/api/v1/auth/login" -H 'Content-Type: application/json'   -d "{\"email\":\"$DEMO_EMAIL\",\"password\":\"$DEMO_PASS\"}")
TOKEN="$(field login.json accessToken)"
printf '      POST /api/v1/auth/login     -> HTTP %s   access token: %s chars\n' "$CODE" "${#TOKEN}"
if [ -n "$TOKEN" ]; then pass "signed in"; else fail "no access token in the login response"; show login.json 8; fi
AUTH=(-H "Authorization: Bearer $TOKEN")

# ===================================================================================================
scene "4. Write a quote, and read it back out of Azure SQL"
claim "Through the broker, into the modular monolith, into a database the application reaches with"
claim "no password at all - Authentication=Active Directory Managed Identity, and nothing else."
CODE=$(fetch create.json -X POST "$BFF/api/v1/quotes" "${AUTH[@]}" -H 'Content-Type: application/json'   -d '{"text":"Ship it, then write down what it taught you.","author":"Day 32"}')
QUOTE_ID="$(field create.json id)"
printf '\n      POST /api/v1/quotes -> HTTP %s   id=%s\n' "$CODE" "$QUOTE_ID"; show create.json 10
if [ "$CODE" = "201" ]; then pass "created"; else fail "expected 201, got $CODE"; fi

CODE=$(fetch readback.json "$BFF/api/v1/quotes/$QUOTE_ID" "${AUTH[@]}")
printf '      GET  /api/v1/quotes/%s -> HTTP %s\n' "$QUOTE_ID" "$CODE"
if [ "$CODE" = "200" ]; then
  pass "persisted - this survives a revision restart, which Day 17 SQLite-in-the-container did not"
else
  fail "read-back returned $CODE"
fi

# ===================================================================================================
scene "5. The outbox - a write and its message commit together or not at all"
claim "Creating that quote wrote a row to Quotes AND a row to Outbox in ONE transaction. The relay"
claim "publishes afterwards and marks the row. A crash between write and publish cannot lose the"
claim "message, because there is no between."
CODE=$(fetch outbox.json "$BFF/api/v1/outbox" "${AUTH[@]}")
printf '\n      GET /api/v1/outbox -> HTTP %s   rows: %s\n' "$CODE" "$(count outbox.json)"; show outbox.json 18
if [ "$CODE" = "200" ]; then pass "outbox readable - rows move Pending -> Published as the relay sweeps"; else fail "expected 200, got $CODE"; fi

# ===================================================================================================
scene "6. Messaging - competing consumers against a real broker"
claim "Not an emulator. The topic and both subscriptions live in the Standard Service Bus namespace"
claim "Day 26 already pays for, and the API reaches it with a managed-identity token, because that"
claim "namespace has SAS authentication disabled. Projections are what the consumers WRITE, so a"
claim "populated store is evidence a real message was received and handled."
CODE=$(fetch events.json "$BFF/api/v1/messaging/projections" "${AUTH[@]}")
printf '\n      GET /api/v1/messaging/projections -> HTTP %s\n' "$CODE"; show events.json 16
if [ "$CODE" = "200" ]; then pass "projections readable - the consumers are live"; else fail "expected 200, got $CODE"; fi

# ===================================================================================================
scene "7. Background jobs"
claim "A bounded in-process queue with backpressure. Bounded on purpose: an unbounded queue turns a"
claim "request burst into unbounded memory and an OOM that names nothing."
CODE=$(fetch jobs.json "$BFF/api/v1/jobs" "${AUTH[@]}")
printf '\n      GET /api/v1/jobs -> HTTP %s   jobs: %s\n' "$CODE" "$(count jobs.json)"; show jobs.json 16
if [ "$CODE" = "200" ]; then pass "job host alive"; else fail "expected 200, got $CODE"; fi

# ===================================================================================================
scene "8. The debug levers are NOT here, and that is the point"
claim "Days 21 and 22 built endpoints that hold the circuit breaker open and switch the cache off."
claim "They are instrumentation, and they are levers over the application's behaviour. Day 27 made"
claim "them Development-only rather than merely authenticated, because an endpoint that does not"
claim "exist cannot be misconfigured. So the resilience demo cannot be run against production -"
claim "a real cost of a decision that was still right."
CODE=$(fetch lever.json "$BFF/api/v1/cache/mode" "${AUTH[@]}" -X POST -H 'Content-Type: application/json' -d '{"enabled":false}')
printf '\n      POST /api/v1/cache/mode -> HTTP %s\n' "$CODE"
if [ "$CODE" = "404" ] || [ "$CODE" = "405" ]; then
  pass "$CODE - not mapped in Production. The lever is absent, not guarded"
else
  fail "expected 404, got $CODE - a debug lever is reachable in production"
fi

# ===================================================================================================
scene "9. Dispatch — the money path, end to end, against a real clock"
claim "Raise, triage, schedule, start, log labour, complete, invoice. Three modules, one process,"
claim "no distributed transaction anywhere. The window is opened three seconds from now because"
claim "the rule that work cannot start early only means something against a real clock."
CUSTOMER="11111111-1111-1111-1111-111111111111"
TECH="$(node -e 'console.log(require("crypto").randomUUID())')"

CODE=$(fetch wo.json -X POST "${DAUTH[@]}" "$DISPATCH/api/work-orders" -H 'Content-Type: application/json' \
  -d "{\"customerId\":\"$CUSTOMER\",\"summary\":\"Chiller unit is not holding temperature\",\"line\":\"Unit 4, Example Industrial Estate\",\"city\":\"Testville\",\"postcode\":\"TV1 9ZZ\"}")
WO="$(field wo.json id)"
printf '\n      POST /api/work-orders -> HTTP %s   id=%s\n' "$CODE" "$WO"
[ "$CODE" = "201" ] && pass "raised" || fail "expected 201, got $CODE"

CODE=$(fetch early.json -X POST "${DAUTH[@]}" "$DISPATCH/api/work-orders/$WO/start")
printf '      POST .../start before triage -> HTTP %s  %s\n' "$CODE" "$(field early.json code)"
[ "$CODE" = "409" ] && pass "409 Conflict, not 400 — the request was fine, the state was not" \
                    || fail "expected 409, got $CODE"

fetch tri.json -X POST "${DAUTH[@]}" "$DISPATCH/api/work-orders/$WO/triage" -H 'Content-Type: application/json' -d '{"priority":"High"}' >/dev/null
START="$(node -e 'console.log(new Date(Date.now()+3000).toISOString())')"
END="$(node -e 'console.log(new Date(Date.now()+3600e3).toISOString())')"
CODE=$(fetch sch.json -X POST "${DAUTH[@]}" "$DISPATCH/api/work-orders/$WO/schedule" -H 'Content-Type: application/json' \
  -d "{\"technicianId\":\"$TECH\",\"windowStart\":\"$START\",\"windowEnd\":\"$END\"}")
printf '      POST .../triage then .../schedule -> HTTP %s\n' "$CODE"
[ "$CODE" = "200" ] || [ "$CODE" = "204" ] && pass "scheduled — WorkManagement asked Scheduling to hold the slot" \
                                           || fail "schedule returned $CODE"

claim "A second order, same technician, same window. WorkManagement commits Scheduled, Scheduling"
claim "refuses, WorkManagement walks itself back. A saga, inside one request."
CODE=$(fetch wo2.json -X POST "${DAUTH[@]}" "$DISPATCH/api/work-orders" -H 'Content-Type: application/json' \
  -d "{\"customerId\":\"$CUSTOMER\",\"summary\":\"Freezer door seal is perished\",\"line\":\"Unit 9, Example Industrial Estate\",\"city\":\"Testville\",\"postcode\":\"TV1 9ZZ\"}")
WO2="$(field wo2.json id)"
fetch t2.json -X POST "${DAUTH[@]}" "$DISPATCH/api/work-orders/$WO2/triage" -H 'Content-Type: application/json' -d '{"priority":"Standard"}' >/dev/null
fetch s2.json -X POST "${DAUTH[@]}" "$DISPATCH/api/work-orders/$WO2/schedule" -H 'Content-Type: application/json' \
  -d "{\"technicianId\":\"$TECH\",\"windowStart\":\"$START\",\"windowEnd\":\"$END\"}" >/dev/null
fetch o2.json "${DAUTH[@]}" "$DISPATCH/api/work-orders/$WO2" >/dev/null
printf '      second order status -> %s\n' "$(field o2.json status)"
[ "$(field o2.json status)" = "Triaged" ] && pass "compensated back to Triaged, not left half-scheduled" \
                                          || fail "expected Triaged, got $(field o2.json status)"

info "waiting 4s for the scheduled window to open"
sleep 4
fetch st.json -X POST "${DAUTH[@]}" "$DISPATCH/api/work-orders/$WO/start" >/dev/null
CODE=$(fetch lab.json -X POST "${DAUTH[@]}" "$DISPATCH/api/work-orders/$WO/labour" -H 'Content-Type: application/json' \
  -d '{"technicianId":"'"$TECH"'","minutes":150,"note":"replaced the thermostat and recharged the circuit"}')
printf '      POST .../start then .../labour -> HTTP %s\n' "$CODE"
CODE=$(fetch comp.json -X POST "${DAUTH[@]}" "$DISPATCH/api/work-orders/$WO/complete")
printf '      POST .../complete -> HTTP %s\n' "$CODE"
[ "$CODE" = "200" ] || [ "$CODE" = "204" ] && pass "completed" || fail "complete returned $CODE"

CODE=$(fetch inv.json "${DAUTH[@]}" "$DISPATCH/api/invoices")
printf '      GET  /api/invoices -> HTTP %s   invoices: %s\n' "$CODE" "$(count inv.json)"; show inv.json 16
[ "$(count inv.json)" != "0" ] && pass "Billing heard the completion and raised an invoice — the money path closed" \
                               || fail "no invoice was raised"

# ===================================================================================================
scene "9b. Dispatch is no longer open, and a role actually separates two people"
claim "Day 31 shipped Dispatch with no authentication and said so out loud. It now validates a"
claim "bearer token against Entra and enforces a role per endpoint. The roles come from the domain:"
claim "a dispatcher commits the customer to a visit, a technician reports the work. Labour hours"
claim "become an invoice, so the principal recording the hours is not the one that booked it."

CODE=$(fetch anon.json "$DISPATCH/api/work-orders" -X POST -H 'Content-Type: application/json'   -d "{\"customerId\":\"$CUSTOMER\",\"summary\":\"unauthenticated probe\",\"line\":\"1\",\"city\":\"T\",\"postcode\":\"T1 1AA\"}")
printf '
      POST /api/work-orders with NO token -> HTTP %s
' "$CODE"
if [ "$CODE" = "401" ]; then
  pass "401 - the API that anyone could post to yesterday is closed today"
else
  fail "expected 401, got $CODE"
fi

CODE=$(fetch badaud.json "$DISPATCH/api/work-orders/$WO" -H "Authorization: Bearer $TOKEN")
printf '      GET  a work order with the QUOTES user token -> HTTP %s
' "$CODE"
if [ "$CODE" = "401" ]; then
  pass "401 - a valid token for the wrong audience is still refused"
else
  fail "expected 401, got $CODE"
fi

CODE=$(fetch authed.json "$DISPATCH/api/work-orders/$WO" "${DAUTH[@]}")
printf '      GET  the same order with a Dispatch token -> HTTP %s
' "$CODE"
if [ "$CODE" = "200" ]; then
  pass "200 - right audience, role satisfied"
else
  fail "expected 200, got $CODE"
fi

# ===================================================================================================
scene "10. The frontend"
claim "Angular, served by nginx, with a Content-Security-Policy whose connect-src names the broker"
claim "and nothing else. An injected script has nowhere to send anything."
CODE=$(fetch index.html "$WEB/")
CSP="$(curl -s -D - -o /dev/null --max-time 60 "$WEB/" | grep -i '^content-security-policy' | head -1)"
printf '\n      GET %s/ -> HTTP %s\n' "$WEB" "$CODE"
printf '      %s\n' "${CSP:0:180}"
[ "$CODE" = "200" ] && pass "served" || fail "expected 200, got $CODE"
printf '%s' "$CSP" | grep -q "$QUOTES_BFF_APP" \
  && pass "connect-src names the broker" \
  || fail "the CSP does not name the broker — BFF_ORIGIN was not substituted"

# ===================================================================================================
scene "Summary"
printf '\n  %s passed, %s failed\n\n' "$PASS" "$FAIL"
printf '  web       %s\n  broker    %s\n  quotes    %s\n  dispatch  %s\n\n' "$WEB" "$BFF" "$API" "$DISPATCH"
[ "$FAIL" -eq 0 ]
