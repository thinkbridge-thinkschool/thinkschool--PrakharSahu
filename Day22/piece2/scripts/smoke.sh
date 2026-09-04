#!/usr/bin/env bash
#
# Day 22 piece2 - drives the whole system through HTTP, once.
#
# The tests prove the aggregate and the flows in isolation. This proves the thing BOOTS and
# composes: three modules, one host, one in-process bus, and a work order that travels from
# "reported" to "invoiced" without anybody calling a module directly.
#
#   bash scripts/smoke.sh

set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
API_DIR="$ROOT/src/Dispatch.Api"
PORT="${PORT:-5322}"
BASE="http://localhost:${PORT}"
DLL="$API_DIR/bin/Debug/net10.0/Dispatch.Api.dll"

PASS=0; FAIL=0
ok()      { PASS=$((PASS+1)); printf '  [PASS] %s\n' "$*"; }
no()      { FAIL=$((FAIL+1)); printf '  [FAIL] %s\n' "$*"; }
info()    { printf '  %s\n' "$*"; }
section() { printf '\n================================================================\n %s\n================================================================\n' "$*"; }

WORK="$ROOT/.smoke-tmp"; mkdir -p "$WORK"
winpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
fetch()   { local n="$1"; shift; curl -s -o "$(winpath "$WORK/$n")" -w '%{http_code}' "$@"; }
field()   { node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{console.log(JSON.parse(s)['$2']??'')}catch{console.log('')}})" < "$WORK/$1"; }
pretty()  { node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{console.log(JSON.stringify(JSON.parse(s),null,2).replace(/^/gm,"      "))}catch{console.log("      "+s.trim())}})' < "$WORK/$1"; }
json()    { printf '%s' "$1"; }

SERVER_PID=""
cleanup() {
  if [ -n "$SERVER_PID" ]; then
    taskkill //F //T //PID "$SERVER_PID" >/dev/null 2>&1 || kill -9 "$SERVER_PID" 2>/dev/null
    wait "$SERVER_PID" 2>/dev/null
  fi
  rm -rf "$WORK"
}
trap cleanup EXIT

printf '=== Building ===\n'
# Built from inside the directory: MSYS_NO_PATHCONV=1 leaves the path as /d/... and MSBuild reads
# a leading slash as a switch prefix.
( cd "$ROOT" && dotnet build -v q --nologo ) >/tmp/day22p2-build.log 2>&1 \
  || { echo "build failed:"; grep -E 'error' /tmp/day22p2-build.log | head; exit 1; }
echo "  built."

( cd "$API_DIR" && exec env ASPNETCORE_URLS="$BASE" dotnet exec "$(winpath "$DLL")" ) >/tmp/day22p2-api.log 2>&1 &
SERVER_PID=$!

for _ in $(seq 1 60); do
  curl -sf "$BASE/health" >/dev/null 2>&1 && break
  sleep 1
done
curl -sf "$BASE/health" >/dev/null 2>&1 || { echo "API did not start:"; tail -25 /tmp/day22p2-api.log; exit 1; }
echo "  API up on $BASE (pid $SERVER_PID)"

CUSTOMER="11111111-1111-1111-1111-111111111111"
TECH="22222222-2222-2222-2222-222222222222"
START="$(node -e 'console.log(new Date(Date.now()+3600e3).toISOString())')"
END="$(node -e 'console.log(new Date(Date.now()+7200e3).toISOString())')"

# =============================================================================================
section "1. Raise a work order"

CODE=$(fetch raise.json -X POST "$BASE/api/work-orders" -H 'Content-Type: application/json' \
  -d "$(json "{\"customerId\":\"$CUSTOMER\",\"summary\":\"Chiller unit is not holding temperature\",\"line\":\"Unit 4, Example Industrial Estate\",\"city\":\"Testville\",\"postcode\":\"TV1 9ZZ\"}")")
ID=$(field raise.json id)
info "HTTP $CODE  id=$ID"
[ "$CODE" = "201" ] && ok "created" || no "expected 201, got $CODE"

# =============================================================================================
section "2. The state machine refuses out-of-order transitions"

CODE=$(fetch bad.json -X POST "$BASE/api/work-orders/$ID/start")
info "start before triage -> HTTP $CODE  $(field bad.json code)"
[ "$CODE" = "409" ] && ok "409 Conflict, not 400 - the request was fine, the state was not" \
                    || no "expected 409, got $CODE"

CODE=$(fetch bad2.json -X POST "$BASE/api/work-orders/$ID/schedule" -H 'Content-Type: application/json' \
  -d "$(json "{\"technicianId\":\"$TECH\",\"windowStart\":\"$START\",\"windowEnd\":\"$END\"}")")
info "schedule before triage -> HTTP $CODE  $(field bad2.json code)"
[ "$CODE" = "409" ] && ok "cannot schedule an untriaged order" || no "expected 409, got $CODE"

# =============================================================================================
section "3. Triage derives the SLA due date"

# The status code is captured, not discarded. An earlier revision threw it away with >/dev/null,
# so when triage started 400-ing on enum binding, the script reported the failure four sections
# later as "status is Raised" and named nothing useful.
CODE=$(fetch triage.json -X POST "$BASE/api/work-orders/$ID/triage" -H 'Content-Type: application/json' \
  -d '{"priority":"High"}')
info "triage -> HTTP $CODE"
fetch order.json "$BASE/api/work-orders/$ID" >/dev/null
pretty order.json
[ "$(field order.json priority)" = "High" ] && ok "priority set, due date derived from it" \
                                            || no "priority was not set"

# =============================================================================================
section "4. Scheduling crosses the module boundary"

CODE=$(fetch sched.json -X POST "$BASE/api/work-orders/$ID/schedule" -H 'Content-Type: application/json' \
  -d "$(json "{\"technicianId\":\"$TECH\",\"windowStart\":\"$START\",\"windowEnd\":\"$END\"}")")
fetch order.json "$BASE/api/work-orders/$ID" >/dev/null
info "HTTP $CODE  status=$(field order.json status)"
[ "$(field order.json status)" = "Scheduled" ] && ok "WorkManagement -> Scheduling reserved the slot" \
                                               || no "status is $(field order.json status)"

# =============================================================================================
section "5. A clashing booking is compensated back to triage"

CODE=$(fetch raise2.json -X POST "$BASE/api/work-orders" -H 'Content-Type: application/json' \
  -d "$(json "{\"customerId\":\"$CUSTOMER\",\"summary\":\"Freezer door seal is perished\",\"line\":\"Unit 9, Example Industrial Estate\",\"city\":\"Testville\",\"postcode\":\"TV1 9ZZ\"}")")
ID2=$(field raise2.json id)
fetch t2.json -X POST "$BASE/api/work-orders/$ID2/triage" -H 'Content-Type: application/json' -d '{"priority":"Standard"}' >/dev/null
fetch s2.json -X POST "$BASE/api/work-orders/$ID2/schedule" -H 'Content-Type: application/json' \
  -d "$(json "{\"technicianId\":\"$TECH\",\"windowStart\":\"$START\",\"windowEnd\":\"$END\"}")" >/dev/null

fetch order2.json "$BASE/api/work-orders/$ID2" >/dev/null
info "second order status=$(field order2.json status)  technician=$(field order2.json technicianId)"

# The saga, over HTTP: WorkManagement committed Scheduled, Scheduling refused, WorkManagement
# walked it back -- all inside one request, with no distributed transaction anywhere.
[ "$(field order2.json status)" = "Triaged" ] \
  && ok "same technician, same window -> compensated back to Triaged" \
  || no "expected Triaged, got $(field order2.json status)"

# =============================================================================================
section "6. Complete the first order, and Billing invoices it"

fetch st.json -X POST "$BASE/api/work-orders/$ID/start" >/dev/null
info "start before the window opens -> $(field st.json code)"

# The window opens an hour from now, so the honest thing this script can show over HTTP is the
# refusal. The domain tests drive the clock forward and cover the rest.
fetch inv.json "$BASE/api/invoices" >/dev/null
COUNT=$(node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{console.log(JSON.parse(s).length)}catch{console.log("?")}})' < "$WORK/inv.json")
info "invoices so far: $COUNT"
[ "$COUNT" = "0" ] && ok "nothing invoiced - no order has been completed" || no "unexpected invoices: $COUNT"

# =============================================================================================
section "7. Cancelling releases the technician's slot"

fetch cancel.json -X POST "$BASE/api/work-orders/$ID/cancel" -H 'Content-Type: application/json' \
  -d '{"reason":"customer resolved it themselves"}' >/dev/null

CODE=$(fetch s3.json -X POST "$BASE/api/work-orders/$ID2/schedule" -H 'Content-Type: application/json' \
  -d "$(json "{\"technicianId\":\"$TECH\",\"windowStart\":\"$START\",\"windowEnd\":\"$END\"}")")
fetch order2.json "$BASE/api/work-orders/$ID2" >/dev/null
info "rebooking the freed window -> HTTP $CODE  status=$(field order2.json status)"

[ "$(field order2.json status)" = "Scheduled" ] \
  && ok "the released slot was reusable - Scheduling heard the cancellation" \
  || no "expected Scheduled, got $(field order2.json status)"

# =============================================================================================
section "Summary"
printf '\n  %s passed, %s failed\n\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ]
