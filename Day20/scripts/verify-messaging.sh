#!/usr/bin/env bash
#
# Day 19 — end-to-end proof against a running Service Bus broker.
#
# Proves, over HTTP and AMQP, the five things the exercise asks for:
#   1. publish to a TOPIC, and both subscriptions receive their own copy (fan-out)
#   2. competing consumers share the load without double-processing
#   3. a duplicate MessageId is suppressed — per subscription, not globally
#   4. a poison message is retried to MaxDeliveryCount
#   5. …and then lands in that subscription's dead-letter queue
#
# Works unchanged against the emulator or a real Azure namespace — only
# ServiceBus__ConnectionString differs.
#
#   ./scripts/start-emulator.sh
#   ./scripts/verify-messaging.sh

set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_DIR="$(cd "$SCRIPT_DIR/../backend" && pwd)"
PORT="${PORT:-5287}"
BASE="http://localhost:${PORT}"
DLL="$BACKEND_DIR/bin/Debug/net10.0/QuotesApi.dll"

SB_CONNECTION="${ServiceBus__ConnectionString:-Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;}"

PASS=0; FAIL=0
ok()      { PASS=$((PASS+1)); printf '  [PASS] %s\n' "$*"; }
no()      { FAIL=$((FAIL+1)); printf '  [FAIL] %s\n' "$*"; }
section() { printf '\n\n================================================================\n %s\n================================================================\n' "$*"; }
step()    { printf '\n--- %s\n' "$*"; }

WORK="$SCRIPT_DIR/../.verify-tmp"; mkdir -p "$WORK"
winpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
# curl on Windows is the native binary and cannot open MSYS paths; bash and node read the
# MSYS form. Same split as Day 17 and Day 18.
fetch()   { local n="$1"; shift; curl -s -o "$(winpath "$WORK/$n")" -w '%{http_code}' "$@"; }
field()   { node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{console.log(JSON.parse(s)['$2']??'')}catch{console.log('')}})" < "$WORK/$1"; }
pretty()  { node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{console.log(JSON.stringify(JSON.parse(s),null,2).replace(/^/gm,"      "))}catch{console.log("      "+s.trim())}})' < "$WORK/$1"; }

SERVER_PID=""
DB="msg-verify-$$.db"
cleanup() {
  if [ -n "$SERVER_PID" ]; then
    kill "$SERVER_PID" 2>/dev/null
    taskkill //F //T //PID "$SERVER_PID" >/dev/null 2>&1
    wait "$SERVER_PID" 2>/dev/null
  fi
  rm -f "$BACKEND_DIR/$DB" "$BACKEND_DIR/$DB"-* 2>/dev/null
}
trap cleanup EXIT

printf '=== Building ===\n'
dotnet build "$BACKEND_DIR" -v q --nologo >/tmp/msg-build.log 2>&1 \
  || { echo "build failed:"; grep -E 'error' /tmp/msg-build.log | head; exit 1; }
echo "  built."

printf '\n=== Starting the API with messaging enabled ===\n'
( cd "$BACKEND_DIR" && exec env \
    DOTNET_ENVIRONMENT=Development \
    Jwt__Key='day19-verify-signing-key-at-least-32-bytes-long' \
    ASPNETCORE_URLS="$BASE" \
    ConnectionStrings__DefaultConnection="Data Source=${DB}" \
    ServiceBus__ConnectionString="$SB_CONNECTION" \
    dotnet exec "$DLL" ) >/tmp/msg-api.log 2>&1 &
SERVER_PID=$!

for _ in $(seq 1 60); do curl -sf -o /dev/null "$BASE/health" 2>/dev/null && break; sleep 1; done
curl -sf -o /dev/null "$BASE/health" || { echo "API did not start:"; tail -30 /tmp/msg-api.log; exit 1; }
echo "  up."

# Consumers connect asynchronously after the host starts.
sleep 3
CONSUMERS=$(grep -c "Consumer .* started on topic" /tmp/msg-api.log)
printf '  competing consumers started: %s\n' "$CONSUMERS"
[ "$CONSUMERS" -ge 4 ] \
  && ok "$CONSUMERS consumers running (2 subscriptions x 2 consumers each)" \
  || no "expected 4 consumers, saw $CONSUMERS"

step "Authenticate (publishing requires a token)"
EMAIL="day19-$(date +%s)@example.invalid"
fetch reg.json -X POST "$BASE/api/auth/register" -H 'Content-Type: application/json' \
  -d "{\"email\":\"${EMAIL}\",\"password\":\"Day19-Verify-Passw0rd!\"}" >/dev/null
TOKEN=$(field reg.json accessToken)
[ -n "$TOKEN" ] && ok "registered" || { no "registration failed"; exit 1; }
AUTH="Authorization: Bearer $TOKEN"

step "Start from an empty dead-letter queue"
for SUB in audit search-index; do
  P=$(fetch "purge-$SUB.json" -X DELETE "$BASE/api/messaging/dlq/$SUB" -H "$AUTH")
  printf '  purged %s: %s\n' "$SUB" "$(field "purge-$SUB.json" purged)"
done

# =========================================================================================
section "1. Publish to the topic — both subscriptions receive it"

step "POST /api/messaging/publish (5 events)"
PUB=$(fetch pub.json -X POST "$BASE/api/messaging/publish" -H "$AUTH" \
  -H 'Content-Type: application/json' -d '{"count":5}')
printf '  HTTP %s, published %s\n' "$PUB" "$(field pub.json published)"
[ "$PUB" = "202" ] && ok "publish accepted" || no "publish -> $PUB"

step "Wait for both subscriptions to drain"
for _ in $(seq 1 30); do
  fetch proj.json "$BASE/api/messaging/projections" >/dev/null
  # Defaulted to 0: an empty value would make [ "" -ge 5 ] a syntax error, and the run would
  # die on a transient parse rather than reporting a failed check.
  A=$(node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{console.log(JSON.parse(s).subscriptions[0].processed)}catch{console.log(0)}})' < "$WORK/proj.json")
  S=$(node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{console.log(JSON.parse(s).subscriptions[1].processed)}catch{console.log(0)}})' < "$WORK/proj.json")
  A=${A:-0}; S=${S:-0}
  [ "$A" -ge 5 ] && [ "$S" -ge 5 ] && break
  sleep 1
done
printf '  audit processed=%s   search-index processed=%s\n' "$A" "$S"

# The heart of a topic: ONE publish, TWO independent readers, both with output. A queue
# would have given the message to one of them and the other would have seen nothing.
[ "$A" -ge 5 ] && ok "audit subscription received all 5" || no "audit received $A of 5"
[ "$S" -ge 5 ] && ok "search-index subscription received all 5" || no "search-index received $S of 5"

step "Competing consumers shared the work without double-processing"
grep -oE "\[(audit|search-index)#[0-9]+\] completed" /tmp/msg-api.log | sort | uniq -c | sed 's/^/      /'
DISTINCT=$(grep -oE "\[(audit|search-index)#[0-9]+\] completed" /tmp/msg-api.log | sort -u | wc -l)
[ "$DISTINCT" -ge 2 ] \
  && ok "$DISTINCT distinct consumers took work — the broker shared it out" \
  || no "only $DISTINCT consumer did any work"

# =========================================================================================
section "2. Idempotency — the same MessageId twice"

step "Publish the SAME EventId three times"
fetch dup.json -X POST "$BASE/api/messaging/publish" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"count":3,"eventId":"duplicate-demo-fixed-id"}' >/dev/null
printf '  message ids: '; node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{console.log(JSON.parse(s).messageIds.join(", "))})' < "$WORK/dup.json"

sleep 6
fetch proj2.json "$BASE/api/messaging/projections" >/dev/null
DUP_A=$(node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const o=JSON.parse(s);console.log(o.subscriptions[0].duplicatesSuppressed)})' < "$WORK/proj2.json")
DUP_S=$(node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const o=JSON.parse(s);console.log(o.subscriptions[1].duplicatesSuppressed)})' < "$WORK/proj2.json")

printf '  duplicates suppressed — audit: %s   search-index: %s\n' "$DUP_A" "$DUP_S"
[ "$DUP_A" -ge 2 ] && ok "audit ran the handler once and suppressed $DUP_A repeats" \
                   || no "audit suppressed $DUP_A (expected >= 2)"

# The subtle one. Both subscriptions see the SAME MessageId, and each must process it once.
# Dedupe keyed on the id alone would let audit's claim suppress search-index entirely.
[ "$DUP_S" -ge 2 ] \
  && ok "search-index independently suppressed $DUP_S — dedupe is per subscription, not global" \
  || no "search-index suppressed $DUP_S (expected >= 2)"

grep -q "Duplicate suppressed on 'search-index'" /tmp/msg-api.log \
  && ok "log confirms search-index deduped on its own key" \
  || no "no per-subscription dedupe evidence in the log"

# =========================================================================================
section "3. Poison message -> retried -> dead-lettered"

step "Publish a poison event (handler throws on every delivery)"
fetch poison.json -X POST "$BASE/api/messaging/publish" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"count":1,"poison":true,"eventId":"poison-demo"}' >/dev/null
POISON_ID=$(node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{console.log(JSON.parse(s).messageIds[0])})' < "$WORK/poison.json")
printf '  poison MessageId: %s\n' "$POISON_ID"

step "Watch it fail and be retried"
# MaxDeliveryCount is 3 on the subscription, so: three attempts, then the broker gives up.
for _ in $(seq 1 40); do
  ATTEMPTS=$(grep -c "MessageId $POISON_ID failed on attempt" /tmp/msg-api.log)
  [ "$ATTEMPTS" -ge 6 ] && break     # 3 attempts x 2 subscriptions
  sleep 1
done
grep -oE "\[(audit|search-index)#[0-9]+\] MessageId $POISON_ID failed on attempt [0-9]+ of [0-9]+" /tmp/msg-api.log \
  | sed 's/^/      /' | head -8
[ "$ATTEMPTS" -ge 3 ] && ok "retried $ATTEMPTS times before the broker gave up" \
                      || no "only $ATTEMPTS delivery attempts observed"

step "It landed in the dead-letter queue of BOTH subscriptions"
for SUB in audit search-index; do
  FOUND=0
  for _ in $(seq 1 30); do
    fetch "dlq-$SUB.json" "$BASE/api/messaging/dlq/$SUB" >/dev/null
    COUNT=$(field "dlq-$SUB.json" deadLetterCount)
    [ "${COUNT:-0}" -ge 1 ] && { FOUND=1; break; }
    sleep 2
  done

  if [ "$FOUND" = "1" ]; then
    ok "$SUB DLQ contains $COUNT message(s)"
    pretty "dlq-$SUB.json"
    grep -q "MaxDeliveryCountExceeded" "$WORK/dlq-$SUB.json" \
      && ok "$SUB: DeadLetterReason = MaxDeliveryCountExceeded (the broker moved it, not the app)" \
      || no "$SUB: unexpected DeadLetterReason"
  else
    no "$SUB DLQ stayed empty"
  fi
done

# =========================================================================================
section "4. Malformed message -> dead-lettered immediately, never retried"

step "Publish a body no consumer can parse"
fetch bad.json -X POST "$BASE/api/messaging/publish" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"count":1,"malformed":true,"eventId":"malformed-demo"}' >/dev/null

for _ in $(seq 1 30); do
  fetch dlq-audit-2.json "$BASE/api/messaging/dlq/audit" >/dev/null
  grep -q "MalformedPayload" "$WORK/dlq-audit-2.json" && break
  sleep 2
done

if grep -q "MalformedPayload" "$WORK/dlq-audit-2.json"; then
  ok "dead-lettered with DeadLetterReason = MalformedPayload"

  # The distinction that matters: a payload that cannot be parsed will not parse on the next
  # delivery either. Retrying it three times wastes two deliveries and buries the real cause
  # under MaxDeliveryCountExceeded.
  RETRIES=$(grep -c "MessageId malformed-demo failed on attempt" /tmp/msg-api.log || true)
  RETRIES=${RETRIES:-0}
  [ "$RETRIES" -eq 0 ] \
    && ok "never retried — rejected on first delivery, with the real cause preserved" \
    || no "it was retried $RETRIES times; a malformed payload should not be"
else
  no "malformed message did not reach the DLQ with the expected reason"
fi

# =========================================================================================
section "Result"
printf '  %d passed, %d failed\n\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ] || exit 1
