#!/usr/bin/env bash
#
# Day 20 — the crash proof, against a real running process.
#
# The unit tests inject faults into the relay. This does something blunter and more
# convincing: it starts the API, arms a crash, creates a quote, then KILLS THE PROCESS with
# taskkill /F — no shutdown handler, no graceful drain, no chance to finish anything. Then it
# starts a new process against the same database file and shows the message still gets
# published.
#
# What that proves is the one claim the pattern makes: the event survives in the database, so
# the publish step is retryable and cannot lose it.
#
#   ./scripts/verify-outbox.sh

set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_DIR="$(cd "$SCRIPT_DIR/../backend" && pwd)"
PORT="${PORT:-5297}"
BASE="http://localhost:${PORT}"
DLL="$BACKEND_DIR/bin/Debug/net10.0/QuotesApi.dll"

# One database file across BOTH process lifetimes. That is the whole point — the outbox row
# has to outlive the process that wrote it.
DB="outbox-verify-$$.db"

PASS=0; FAIL=0
ok()      { PASS=$((PASS+1)); printf '  [PASS] %s\n' "$*"; }
no()      { FAIL=$((FAIL+1)); printf '  [FAIL] %s\n' "$*"; }
section() { printf '\n\n================================================================\n %s\n================================================================\n' "$*"; }
step()    { printf '\n--- %s\n' "$*"; }

WORK="$SCRIPT_DIR/../.verify-tmp"; mkdir -p "$WORK"
winpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
fetch()   { local n="$1"; shift; curl -s -o "$(winpath "$WORK/$n")" -w '%{http_code}' "$@"; }
field()   { node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{console.log(JSON.parse(s)['$2']??'')}catch{console.log('')}})" < "$WORK/$1"; }
pretty()  { node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{console.log(JSON.stringify(JSON.parse(s),null,2).replace(/^/gm,"      "))}catch{console.log("      "+s.trim())}})' < "$WORK/$1"; }

SERVER_PID=""
start_api() {
  ( cd "$BACKEND_DIR" && exec env \
      DOTNET_ENVIRONMENT=Development \
      Jwt__Key='day20-outbox-verify-signing-key-at-least-32-bytes' \
      ASPNETCORE_URLS="$BASE" \
      ConnectionStrings__DefaultConnection="Data Source=${DB}" \
      dotnet exec "$(winpath "$DLL")" ) >>"$1" 2>&1 &
  SERVER_PID=$!

  # Waits for the log to say it is listening BEFORE probing. Probing first is a race: on a
  # cold start with Debug-level logging the host can take a while, and a fixed number of
  # one-second attempts silently expires just as the port opens — which looks identical to a
  # crash and sends you reading the wrong logs.
  for _ in $(seq 1 90); do
    grep -q "Now listening on" "$1" 2>/dev/null && break
    sleep 1
  done

  for _ in $(seq 1 30); do
    curl -sf "$BASE/health" >/dev/null 2>&1 && return 0
    sleep 1
  done

  echo "API did not start. Last lines:"; tail -25 "$1"; return 1
}

# taskkill /F, not kill. SIGTERM would run StopAsync and let the relay drain, which is the
# opposite of what is being tested. This is a power cut.
hard_kill() {
  taskkill //F //T //PID "$SERVER_PID" >/dev/null 2>&1 || kill -9 "$SERVER_PID" 2>/dev/null
  wait "$SERVER_PID" 2>/dev/null
  SERVER_PID=""
}

cleanup() {
  [ -n "$SERVER_PID" ] && hard_kill
  rm -f "$BACKEND_DIR/$DB" "$BACKEND_DIR/$DB"-* 2>/dev/null
  rm -rf "$WORK"
}
trap cleanup EXIT

printf '=== Building ===\n'
# Built from inside the directory rather than by passing its path. MSYS_NO_PATHCONV=1 (needed
# so Windows tools are not handed mangled paths) means "$BACKEND_DIR" arrives as
# /d/ThinkBridge/... — and MSBuild reads a leading slash as a switch prefix, failing with
# "MSB1001: Unknown switch" while naming nothing that looks like a path.
( cd "$BACKEND_DIR" && dotnet build -v q --nologo ) >/tmp/outbox-build.log 2>&1 \
  || { echo "build failed:"; grep -E 'error' /tmp/outbox-build.log | head; exit 1; }
echo "  built."

# =========================================================================================
section "1. The domain change and the event commit together"

rm -f /tmp/outbox-run1.log
start_api /tmp/outbox-run1.log || exit 1
echo "  process 1 up (pid $SERVER_PID)"

step "Authenticate"
EMAIL="day20-$(date +%s)@example.invalid"
fetch reg.json -X POST "$BASE/api/auth/register" -H 'Content-Type: application/json' \
  -d "{\"email\":\"${EMAIL}\",\"password\":\"Day20-Outbox-Passw0rd!\"}" >/dev/null
TOKEN=$(field reg.json accessToken)
[ -n "$TOKEN" ] && ok "registered" || { no "registration failed"; exit 1; }
AUTH="Authorization: Bearer $TOKEN"

step "Arm the relay to crash BEFORE publishing"
fetch fault.json -X POST "$BASE/api/outbox/faults" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"mode":"BeforePublish","occurrences":50}' >/dev/null
printf '  armed: %s\n' "$(field fault.json mode)"

step "Create a quote (POST /api/quotes)"
CREATED=$(fetch quote.json -X POST "$BASE/api/quotes" -H "$AUTH" -H 'Content-Type: application/json' \
  -d "{\"author\":\"Grace Hopper\",\"text\":\"The most damaging phrase is, we have always done it this way.\"}")
QUOTE_ID=$(field quote.json id)
[ "$CREATED" = "201" ] && ok "quote $QUOTE_ID created" || { no "create -> $CREATED"; pretty quote.json; }

step "The outbox row exists and is Pending"
sleep 2
fetch outbox1.json "$BASE/api/outbox" >/dev/null
PENDING1=$(field outbox1.json pending)
printf '  pending=%s processed=%s\n' "$PENDING1" "$(field outbox1.json processed)"
pretty outbox1.json | head -20

MSG_ID=$(node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const o=JSON.parse(s);console.log(o.recent[0]?.id??"")})' < "$WORK/outbox1.json")
[ "${PENDING1:-0}" -ge 1 ] \
  && ok "event is durably written but NOT yet published (id $MSG_ID)" \
  || no "expected a pending outbox row, saw $PENDING1"

grep -q "staged outbox message" /tmp/outbox-run1.log \
  && ok "log confirms the quote and the outbox row were staged in one transaction" \
  || no "no staging evidence in the log"

# =========================================================================================
section "2. Kill the process mid-flight (taskkill /F — a power cut, not a shutdown)"

step "Confirm the relay really was failing before the publish"
grep -q "FAULT INJECTED: crashing before publishing" /tmp/outbox-run1.log \
  && ok "relay crashed before handing the message to the broker" \
  || no "expected the injected crash in the log"

step "taskkill /F"
hard_kill
sleep 1
curl -sf "$BASE/health" >/dev/null 2>&1 \
  && no "process is somehow still alive" \
  || ok "process 1 is dead — no StopAsync ran, nothing drained"

# =========================================================================================
section "3. Restart — the message is still there and gets published"

step "Start a NEW process against the SAME database file"
rm -f /tmp/outbox-run2.log
start_api /tmp/outbox-run2.log || exit 1
echo "  process 2 up (pid $SERVER_PID)"

# The fault lives in memory, so the new process starts clean — exactly like a real restart
# after fixing whatever caused the crash.
step "The quote survived the crash"
QUOTE_STATUS=$(fetch quote-after.json "$BASE/api/quotes/$QUOTE_ID")
[ "$QUOTE_STATUS" = "200" ] && ok "quote $QUOTE_ID is still there" || no "quote -> $QUOTE_STATUS"

step "…and so did its unpublished event, which the new relay now drains"
DRAINED=0
for _ in $(seq 1 30); do
  fetch outbox2.json "$BASE/api/outbox" >/dev/null
  P=$(field outbox2.json pending)
  [ "${P:-1}" = "0" ] && { DRAINED=1; break; }
  sleep 1
done

fetch msg.json "$BASE/api/outbox/$MSG_ID" >/dev/null
pretty msg.json

if [ "$DRAINED" = "1" ]; then
  ok "the pending event was published after restart — NOTHING WAS LOST"
else
  no "the outbox did not drain after restart (pending=$P)"
fi

STATUS_AFTER=$(field msg.json status)
[ "$STATUS_AFTER" = "Published" ] \
  && ok "message $MSG_ID is now Published" \
  || no "message status is '$STATUS_AFTER'"

grep -q "Published outbox message" /tmp/outbox-run2.log \
  && ok "process 2's relay log confirms the publish" \
  || no "no publish evidence in process 2's log"

# =========================================================================================
section "4. Why this is at-least-once, not exactly-once"

cat <<'NOTE'
      The crash above happened BEFORE the publish, so the message went out exactly once.

      The other crash point cannot be made that clean. If the relay publishes and then dies
      before writing ProcessedAt, the broker has the message but the row still says pending —
      so the restarted relay publishes it AGAIN. There is no way to make "hand it to the
      broker" and "record that we did" atomic, so one of them must be able to happen twice.

      The outbox chooses to duplicate rather than to lose, because a duplicate is recoverable
      by the consumer and a loss is recoverable by nobody. Both copies carry the same
      MessageId — the outbox row's own id — so Day 19's dedupe collapses them into one
      effective delivery.

      That path is proven deterministically in:
        tests/QuotesApi.Outbox.Tests -> Crash_after_publish_before_mark_duplicates_rather_than_loses
NOTE

section "Result"
printf '  %d passed, %d failed\n\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ] || exit 1
