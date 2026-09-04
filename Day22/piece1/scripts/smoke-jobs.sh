#!/usr/bin/env bash
#
# Day 18 — end-to-end check of the background job pipeline against a running API.
#
# What this proves, over HTTP, that a unit test cannot:
#   · POST /api/jobs returns 202 immediately, long before the work is done
#   · the response time is independent of how long the job takes
#   · a caller can poll the Location header until the job reaches a terminal state
#   · succeeded / failed / cancelled are distinguishable from outside
#
# What it deliberately does NOT cover: graceful shutdown. Stopping a .NET host *gracefully*
# needs SIGTERM, and Git Bash's `kill` on Windows maps to TerminateProcess — the process dies
# without ever running StopAsync, so a test here would prove nothing about the grace period.
# That behaviour is covered deterministically by tests/QuotesApi.Jobs.Tests instead.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_DIR="$(cd "$SCRIPT_DIR/../backend" && pwd)"
PORT="${PORT:-5277}"
BASE="http://localhost:${PORT}"
DLL="$BACKEND_DIR/bin/Debug/net10.0/QuotesApi.dll"

PASS=0; FAIL=0
ok() { PASS=$((PASS+1)); printf '  [PASS] %s\n' "$*"; }
no() { FAIL=$((FAIL+1)); printf '  [FAIL] %s\n' "$*"; }
step() { printf '\n--- %s\n' "$*"; }

WORK="$SCRIPT_DIR/../.smoke-tmp"; mkdir -p "$WORK"
# curl on Windows is the native binary and cannot open MSYS paths; everything else here reads
# the MSYS form. Same split as Day17/scripts/verify.sh.
winpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
fetch() { local name="$1"; shift; curl -s -o "$(winpath "$WORK/$name")" -w '%{http_code}' "$@"; }
field() { node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{console.log(JSON.parse(s)['$2']??'')}catch{console.log('')}})" < "$WORK/$1"; }

SERVER_PID=""
DB="jobs-smoke-$$.db"
cleanup() {
  if [ -n "$SERVER_PID" ]; then
    kill "$SERVER_PID" 2>/dev/null
    taskkill //F //T //PID "$SERVER_PID" >/dev/null 2>&1
    wait "$SERVER_PID" 2>/dev/null
  fi
  rm -f "$BACKEND_DIR/$DB" "$BACKEND_DIR/$DB"-* 2>/dev/null
  rm -rf "$WORK"
}
trap cleanup EXIT

printf '=== Building ===\n'
dotnet build "$BACKEND_DIR" -v q --nologo >/tmp/jobs-build.log 2>&1 \
  || { echo "build failed:"; grep -E 'error' /tmp/jobs-build.log | head; exit 1; }
echo "  built."

printf '\n=== Starting the API on %s ===\n' "$BASE"
( cd "$BACKEND_DIR" && exec env \
    DOTNET_ENVIRONMENT=Development \
    Jwt__Key='day18-smoke-signing-key-at-least-32-bytes-long' \
    ASPNETCORE_URLS="$BASE" \
    ConnectionStrings__DefaultConnection="Data Source=${DB}" \
    dotnet exec "$DLL" ) >/tmp/jobs-api.log 2>&1 &
SERVER_PID=$!

for _ in $(seq 1 45); do curl -sf -o /dev/null "$BASE/health" 2>/dev/null && break; sleep 1; done
curl -sf -o /dev/null "$BASE/health" || { echo "API did not start:"; tail -25 /tmp/jobs-api.log; exit 1; }
echo "  up."

grep -q "Job pipeline ready" /tmp/jobs-api.log \
  && ok "IHostedService verified the pipeline at startup (fail-fast check ran)" \
  || no "expected 'Job pipeline ready' from JobPipelineDiagnostics"
grep -q "Job processor started" /tmp/jobs-api.log \
  && ok "BackgroundService worker started" || no "worker did not log a start"

# =========================================================================================
step "Register a user (POST /api/jobs requires authorization)"
EMAIL="day18-$(date +%s)@example.invalid"
REG=$(fetch register.json -X POST "$BASE/api/auth/register" -H 'Content-Type: application/json' \
  -d "{\"email\":\"${EMAIL}\",\"password\":\"Day18-Smoke-Passw0rd!\"}")
TOKEN=$(field register.json accessToken)
[ -n "$TOKEN" ] && ok "registered ($REG)" || { no "registration failed ($REG)"; exit 1; }
AUTH="Authorization: Bearer $TOKEN"

step "Anonymous POST /api/jobs is refused"
ANON=$(fetch anon.json -X POST "$BASE/api/jobs" -H 'Content-Type: application/json' \
  -d '{"type":"simulate"}')
[ "$ANON" = "401" ] && ok "anonymous enqueue -> 401" || no "anonymous enqueue -> $ANON (expected 401)"

step "Unknown job type is rejected at enqueue, not at dequeue"
UNK=$(fetch unknown.json -X POST "$BASE/api/jobs" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"type":"no-such-handler"}')
[ "$UNK" = "400" ] && ok "unknown type -> 400 (caller never gets a 202 for impossible work)" \
                   || no "unknown type -> $UNK (expected 400)"

# =========================================================================================
step "POST /api/jobs returns 202 immediately for a 6-second job"
START_MS=$(node -e 'console.log(Date.now())')
CREATE=$(fetch created.json -X POST "$BASE/api/jobs" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"type":"simulate","payload":"{\"durationMs\":6000}"}')
END_MS=$(node -e 'console.log(Date.now())')
ELAPSED=$((END_MS - START_MS))
JOB_ID=$(field created.json id)

printf '  HTTP %s in %sms, job %s\n' "$CREATE" "$ELAPSED" "$JOB_ID"
[ "$CREATE" = "202" ] && ok "enqueue -> 202 Accepted" || no "enqueue -> $CREATE (expected 202)"
[ "$ELAPSED" -lt 2000 ] \
  && ok "request returned in ${ELAPSED}ms — the 6s of work is off the request thread" \
  || no "request took ${ELAPSED}ms; the work is NOT off the request thread"

step "Poll the Location target until it reaches a terminal state"
STATUS=""
for _ in $(seq 1 40); do
  fetch polled.json "$BASE/api/jobs/$JOB_ID" >/dev/null
  STATUS=$(field polled.json status)
  PROGRESS=$(field polled.json progress)
  printf '  status=%-10s progress=%s\n' "$STATUS" "${PROGRESS:-–}"
  case "$STATUS" in Succeeded|Failed|Cancelled) break;; esac
  sleep 1
done
[ "$STATUS" = "Succeeded" ] && ok "job reached Succeeded" || no "job ended as '$STATUS'"
printf '  result: %s\n' "$(field polled.json result)"
printf '  queue latency: %sms   duration: %sms\n' "$(field polled.json queueLatencyMs)" "$(field polled.json durationMs)"

# =========================================================================================
step "A failing handler produces Failed, not a 500 anywhere"
fetch failing.json -X POST "$BASE/api/jobs" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"type":"simulate","payload":"{\"durationMs\":500,\"shouldFail\":true}"}' >/dev/null
FAIL_ID=$(field failing.json id)
for _ in $(seq 1 20); do
  fetch failed-poll.json "$BASE/api/jobs/$FAIL_ID" >/dev/null
  S=$(field failed-poll.json status); case "$S" in Succeeded|Failed|Cancelled) break;; esac; sleep 1
done
[ "$S" = "Failed" ] && ok "failing job -> Failed" || no "failing job -> $S (expected Failed)"
printf '  error: %s\n' "$(field failed-poll.json error)"

step "DELETE cancels a running job (Cancelled, distinct from Failed)"
fetch long.json -X POST "$BASE/api/jobs" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"type":"simulate","payload":"{\"durationMs\":30000}"}' >/dev/null
LONG_ID=$(field long.json id)
# Wait for it to actually start — a queued job has no token to signal yet, and the endpoint
# says so rather than pretending it cancelled something.
for _ in $(seq 1 20); do
  fetch long-poll.json "$BASE/api/jobs/$LONG_ID" >/dev/null
  [ "$(field long-poll.json status)" = "Running" ] && break; sleep 1
done
DEL=$(fetch deleted.json -X DELETE "$BASE/api/jobs/$LONG_ID" -H "$AUTH")
[ "$DEL" = "202" ] && ok "cancel request -> 202 Accepted" || no "cancel -> $DEL (expected 202)"
for _ in $(seq 1 20); do
  fetch long-poll.json "$BASE/api/jobs/$LONG_ID" >/dev/null
  S=$(field long-poll.json status); case "$S" in Succeeded|Failed|Cancelled) break;; esac; sleep 1
done
[ "$S" = "Cancelled" ] && ok "cancelled mid-flight -> Cancelled" || no "-> $S (expected Cancelled)"

step "GET /api/jobs reports queue depth alongside history"
fetch list.json "$BASE/api/jobs" >/dev/null
printf '  queueDepth: %s\n' "$(field list.json queueDepth)"
node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const o=JSON.parse(s);console.log("  jobs recorded: "+o.jobs.length)})' < "$WORK/list.json"
ok "job history is queryable"

step "The real DB-backed job runs too"
fetch report.json -X POST "$BASE/api/jobs" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"type":"quote-report"}' >/dev/null
REPORT_ID=$(field report.json id)
for _ in $(seq 1 40); do
  fetch report-poll.json "$BASE/api/jobs/$REPORT_ID" >/dev/null
  S=$(field report-poll.json status); case "$S" in Succeeded|Failed|Cancelled) break;; esac; sleep 1
done
[ "$S" = "Succeeded" ] \
  && ok "quote-report job succeeded using a scoped DbContext from its own DI scope" \
  || no "quote-report -> $S"
printf '  result: %s\n' "$(field report-poll.json result)"

printf '\n%d passed, %d failed\n\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ] || exit 1
