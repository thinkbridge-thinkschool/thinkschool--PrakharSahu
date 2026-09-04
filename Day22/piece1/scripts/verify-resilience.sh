#!/usr/bin/env bash
#
# Day 22 - the resilience proof, against a real running process.
#
# The unit tests drive the pipeline in isolation with a delegate standing in for the network.
# This does the same work over real sockets: a real Kestrel, real HTTP status codes, real
# connection handling, and a dependency that is switched from healthy to broken and back while
# the process keeps running.
#
# That last part is the point. A circuit breaker's state only means anything across a continuous
# timeline, so a proof that restarts the app between arms proves nothing -- the restart resets
# the breaker and erases the thing being measured.
#
# What it shows, in order:
#   1. Healthy baseline                     -- circuit closed, calls succeed
#   2. Idempotency                          -- GET retried 3x, POST retried 0x, same fault
#   3. Sustained failure                    -- circuit CLOSED -> OPEN
#   4. Open circuit                         -- rejects in microseconds, never touches the network
#   5. Recovery                             -- OPEN -> HALF-OPEN -> CLOSED
#   6. Failed recovery                      -- OPEN -> HALF-OPEN -> OPEN when still unhealthy
#   7. Attempt timeout                      -- one slow call bounded at the attempt timeout
#   8. Bulkhead                             -- concurrent load shed once permits and queue are full
#
#   ./scripts/verify-resilience.sh

set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_DIR="$(cd "$SCRIPT_DIR/../backend" && pwd)"
PORT="${PORT:-5311}"
BASE="http://localhost:${PORT}"
DLL="$BACKEND_DIR/bin/Debug/net10.0/QuotesApi.dll"
DB="resilience-verify-$$.db"

PASS=0; FAIL=0
ok()      { PASS=$((PASS+1)); printf '  [PASS] %s\n' "$*"; }
no()      { FAIL=$((FAIL+1)); printf '  [FAIL] %s\n' "$*"; }
info()    { printf '  %s\n' "$*"; }
section() { printf '\n\n================================================================\n %s\n================================================================\n' "$*"; }
step()    { printf '\n--- %s\n' "$*"; }

WORK="$SCRIPT_DIR/../.verify-tmp"; mkdir -p "$WORK"
winpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
fetch()   { local n="$1"; shift; curl -s -o "$(winpath "$WORK/$n")" -w '%{http_code}' "$@"; }
field()   { node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{console.log(JSON.parse(s)['$2']??'')}catch{console.log('')}})" < "$WORK/$1"; }
pretty()  { node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{console.log(JSON.stringify(JSON.parse(s),null,2).replace(/^/gm,"      "))}catch{console.log("      "+s.trim())}})' < "$WORK/$1"; }

# ---------------------------------------------------------------------------------------------
# Pipeline settings for the demo, injected as environment variables so the shipped defaults are
# not edited to make a script pass.
#
# Every one of them is smaller than a production value would be. A breaker that needs fifty
# failures over a five-minute window and then stays open for a minute is correct in production
# and useless in a demo, because nobody watches long enough to see it recover.
#
# MinimumThroughput is 8 rather than 4 on purpose: one idempotent call makes four attempts, so a
# threshold of 4 would let a single GET open the breaker on its own retries. Section 2 needs the
# circuit still closed when it finishes.
# ---------------------------------------------------------------------------------------------
export Upstream__FailureRatio=0.5
export Upstream__MinimumThroughput=8
export Upstream__SamplingDuration=00:00:30
export Upstream__BreakDuration=00:00:05
export Upstream__MaxRetryAttempts=3
export Upstream__RetryBaseDelay=00:00:00.100
export Upstream__AttemptTimeout=00:00:01
export Upstream__TotalTimeout=00:00:10
export Upstream__MaxConcurrency=2
export Upstream__MaxQueue=1

SERVER_PID=""
start_api() {
  ( cd "$BACKEND_DIR" && exec env \
      DOTNET_ENVIRONMENT=Development \
      Jwt__Key='day22-resilience-verify-signing-key-at-least-32-bytes' \
      ASPNETCORE_URLS="$BASE" \
      ConnectionStrings__DefaultConnection="Data Source=${DB}" \
      dotnet exec "$(winpath "$DLL")" ) >>"$1" 2>&1 &
  SERVER_PID=$!

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

cleanup() {
  if [ -n "$SERVER_PID" ]; then
    # taskkill, not kill: dotnet exec spawns under the shell and SIGTERM from MSYS does not
    # always reach it. //T takes the tree with it.
    taskkill //F //T //PID "$SERVER_PID" >/dev/null 2>&1 || kill -9 "$SERVER_PID" 2>/dev/null
    wait "$SERVER_PID" 2>/dev/null
  fi
  rm -f "$BACKEND_DIR/$DB" "$BACKEND_DIR/$DB"-* 2>/dev/null
  rm -rf "$WORK"
}
trap cleanup EXIT

# ---- helpers on the running API --------------------------------------------------------------
set_upstream() { fetch fault.json -X POST "$BASE/api/resilience/upstream/faults" \
                   -H 'Content-Type: application/json' -d "$1" >/dev/null; }
circuit()      { fetch state.json "$BASE/api/resilience/state" >/dev/null; field state.json circuitState; }
# Breaker first, log second. The other order leaves the manual "closed" event sitting at the
# head of a freshly-cleared log, where it reads as a real state transition.
reset_all()    { fetch r.json -X POST "$BASE/api/resilience/breaker/close" >/dev/null
                 fetch r.json -X POST "$BASE/api/resilience/reset" >/dev/null; }
# These echo the HTTP status. Do NOT redirect inside the function - an earlier revision had
# ">/dev/null" here, which sent the status code to the void and made every "CODE=$(call_write ...)"
# capture an empty string. Redirect at the call site instead, where it is visible.
call_read()    { fetch "$1" "$BASE/api/resilience/call"; }
call_write()   { fetch "$1" -X POST "$BASE/api/resilience/call-write"; }
stats_field()  { fetch stats.json "$BASE/api/resilience/stats" >/dev/null; field stats.json "$1"; }

transitions() {
  fetch ev.json "$BASE/api/resilience/events?transitionsOnly=true" >/dev/null
  node -e '
    let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{
      const j=JSON.parse(s);
      for (const e of j.events) console.log(`      ${e.at}  ${e.event.toUpperCase().padEnd(12)} ${e.detail}`);
    });' < "$WORK/ev.json"
}

printf '=== Building ===\n'
# Built from inside the directory. MSYS_NO_PATHCONV=1 leaves "$BACKEND_DIR" as /d/ThinkBridge/...
# and MSBuild reads the leading slash as a switch prefix, failing with MSB1001 while naming
# nothing that looks like a path.
( cd "$BACKEND_DIR" && dotnet build -v q --nologo ) >/tmp/day22-verify-build.log 2>&1 \
  || { echo "build failed:"; grep -E 'error' /tmp/day22-verify-build.log | head; exit 1; }
echo "  built."

rm -f /tmp/day22-api.log
start_api /tmp/day22-api.log || exit 1
echo "  API up on $BASE (pid $SERVER_PID)"

step "Pipeline configuration in force"
fetch state.json "$BASE/api/resilience/state" >/dev/null
pretty state.json

# =============================================================================================
section "1. Healthy baseline"

set_upstream '{"mode":"None"}'
reset_all

CODE=$(call_read call.json)
printf '  GET /api/resilience/call -> HTTP %s\n' "$CODE"
pretty call.json

[ "$CODE" = "200" ] && ok "healthy dependency answers 200 through the pipeline" \
                    || no "expected 200, got $CODE"
[ "$(circuit)" = "Closed" ] && ok "circuit is Closed" || no "circuit is $(circuit), expected Closed"

# =============================================================================================
section "2. Retry is idempotent-only"
#
# Same dependency, same fault, same pipeline. The only difference between the two calls is
# whether repeating the operation would have side effects.

set_upstream '{"mode":"ServerError"}'

step "GET (idempotent) against a failing dependency"
reset_all
call_read read.json >/dev/null
fetch ev-get.json "$BASE/api/resilience/events?limit=10" >/dev/null
GET_RETRIES=$(stats_field retries)
info "retries: $GET_RETRIES"
[ "$GET_RETRIES" = "3" ] && ok "GET retried 3 times (1 attempt + 3 retries)" \
                         || no "expected 3 retries, got $GET_RETRIES"

step "POST (not idempotent) against the same failing dependency"
reset_all
call_write write.json >/dev/null
POST_RETRIES=$(stats_field retries)
info "retries: $POST_RETRIES"
[ "$POST_RETRIES" = "0" ] && ok "POST was not retried - one attempt, no duplicate side effects" \
                          || no "expected 0 retries, got $POST_RETRIES"

step "What the pipeline logged during the GET"
# The GET's events, captured before the POST ran. Reading them afterwards would show the POST's,
# which are empty by definition - that is the whole claim being made about it.
pretty ev-get.json

# =============================================================================================
section "3. Sustained failure opens the circuit"

reset_all
set_upstream '{"mode":"ServerError"}'
info "circuit before: $(circuit)"

step "Driving 12 non-idempotent calls at the broken dependency"
# Non-idempotent on purpose: one call, one breaker sample. The arithmetic stays readable
# instead of depending on how many retries each call happened to make.
for i in $(seq 1 12); do
  CODE=$(call_write "c$i.json")
  printf '  call %-2s -> HTTP %-3s  outcome=%-16s elapsed=%sms  circuit=%s\n' \
    "$i" "$CODE" "$(field "c$i.json" outcome)" "$(field "c$i.json" elapsedMs)" "$(circuit)"
done

AFTER=$(circuit)
[ "$AFTER" = "Open" ] && ok "circuit opened under sustained failure" \
                      || no "circuit is $AFTER, expected Open"

step "Breaker state transitions so far"
transitions

# =============================================================================================
section "4. An open circuit rejects without touching the network"

# Re-opened here too, and driven with a tight loop that does not stop to read the circuit state
# between calls.
#
# Section 3's per-call display is worth its cost in readability, but it stretches twelve calls
# across roughly five seconds - one whole break duration. Carrying that circuit into this section
# meant the break expired part-way through, a half-open probe reached the dependency, and
# upstreamFailures moved by one. Correct breaker behaviour, wrong section to observe it in: the
# claim being made here is that an OPEN circuit costs nothing, so the window has to still be open
# for all ten calls.
step "Re-opening the circuit, then firing 10 calls inside the break window"
reset_all
set_upstream '{"mode":"ServerError"}'
for i in $(seq 1 12); do call_write "q$i.json" >/dev/null; done
info "circuit: $(circuit)"

BEFORE_FAILURES=$(stats_field upstreamFailures)
for i in $(seq 1 10); do call_write "o$i.json" >/dev/null; done
AFTER_FAILURES=$(stats_field upstreamFailures)
REJECTIONS=$(stats_field breakerRejections)

info "breakerRejections: $REJECTIONS"
info "upstreamFailures:  $BEFORE_FAILURES -> $AFTER_FAILURES"
info "sample outcome:    $(field o1.json outcome), $(field o1.json elapsedMs)ms"

[ "$BEFORE_FAILURES" = "$AFTER_FAILURES" ] \
  && ok "not one of those calls reached the dependency" \
  || no "upstreamFailures moved from $BEFORE_FAILURES to $AFTER_FAILURES"

[ "$REJECTIONS" -ge 10 ] 2>/dev/null \
  && ok "$REJECTIONS calls rejected by the breaker, in microseconds each" \
  || no "expected at least 10 breaker rejections, got $REJECTIONS"

# =============================================================================================
section "5. Recovery: OPEN -> HALF-OPEN -> CLOSED"

# Re-opened from scratch so the break duration is known to have only just started.
#
# Section 4 takes several seconds, and the break is five. Carrying its circuit forward meant the
# break had already elapsed by the time this section ran, so the "the breaker does not know yet"
# call found a half-open circuit and sailed through - the demo accidentally proved the opposite
# of its own point. Timing that depends on how fast the previous section happened to run is not
# evidence.
step "Re-opening the circuit so the break duration starts now"
reset_all
set_upstream '{"mode":"ServerError"}'
for i in $(seq 1 12); do call_write "p$i.json" >/dev/null; done
info "circuit: $(circuit)"

step "Dependency is repaired, but the breaker does not know yet"
set_upstream '{"mode":"None"}'
call_write imm.json >/dev/null
info "immediate call: outcome=$(field imm.json outcome)  circuit=$(circuit)"
[ "$(field imm.json outcome)" = "CircuitOpen" ] \
  && ok "still rejected - recovery is time-based, not health-based" \
  || no "expected CircuitOpen immediately after repair"

step "Waiting out the 5s break duration"
sleep 6

step "One trial call"
CODE=$(call_write trial.json)
info "HTTP $CODE  outcome=$(field trial.json outcome)  circuit=$(circuit)"

[ "$(circuit)" = "Closed" ] \
  && ok "trial call succeeded and the circuit closed - no operator involved" \
  || no "circuit is $(circuit) after the trial call, expected Closed"

step "Full transition sequence"
transitions

# The LAST three transitions, not all of them.
#
# Section 4 spends more than one break duration hammering the open circuit, so the breaker
# legitimately half-opens mid-section, fails its probe against the still-broken dependency
# and re-opens. That is the breaker working, and an assertion over the whole history would
# call it a failure. The claim this section makes is about how the cycle ENDS: a probe
# succeeded and traffic came back.
SEQ=$(fetch ev.json "$BASE/api/resilience/events?transitionsOnly=true" >/dev/null; \
      node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const j=JSON.parse(s);
        const t=j.events.filter(e=>["opened","half-opened","closed"].includes(e.event)).map(e=>e.event);
        console.log(t.slice(-3).join(" -> "))});' \
      < "$WORK/ev.json")
info "last three transitions: $SEQ"
[ "$SEQ" = "opened -> half-opened -> closed" ] \
  && ok "open -> half-open -> closed: the dependency recovered and traffic was restored" \
  || no "unexpected sequence: $SEQ"

# =============================================================================================
section "6. A failed trial call re-opens the circuit"
#
# Recovery is not assumed on a schedule. If the probe fails, the break starts again -- so a
# dependency that is still down is not re-flooded the instant its timer expires.

reset_all
set_upstream '{"mode":"ServerError"}'
for i in $(seq 1 12); do call_write "r$i.json" >/dev/null; done
info "circuit: $(circuit)"

step "Waiting out the break with the dependency STILL broken"
sleep 6
call_write trial2.json >/dev/null
info "trial call: outcome=$(field trial2.json outcome)  circuit=$(circuit)"

[ "$(circuit)" = "Open" ] \
  && ok "probe failed, circuit re-opened for another full break duration" \
  || no "circuit is $(circuit), expected Open"

step "Transitions"
transitions

# =============================================================================================
section "7. Timeout bounds a slow dependency"

reset_all
# 3s of latency against a 1s attempt timeout. The endpoint honours the cancellation token, so
# the attempt is genuinely cancelled rather than abandoned while still running.
set_upstream '{"mode":"Slow","latencyMs":3000}'

step "POST (no retries) against a dependency that has stopped answering"
CODE=$(call_write slow.json)
ELAPSED=$(field slow.json elapsedMs)
info "HTTP $CODE  outcome=$(field slow.json outcome)  elapsed=${ELAPSED}ms"

[ "$(field slow.json outcome)" = "TimedOut" ] \
  && ok "call was cut off rather than waiting on a dead dependency" \
  || no "expected TimedOut, got $(field slow.json outcome)"

node -e "process.exit(Number('$ELAPSED') < 2500 ? 0 : 1)" \
  && ok "bounded at the 1s attempt timeout, not the 3s the dependency wanted (${ELAPSED}ms)" \
  || no "took ${ELAPSED}ms; the attempt timeout did not bound it"

# =============================================================================================
section "8. Bulkhead sheds load once permits and queue are full"

reset_all
# 600ms is under the 1s attempt timeout, so admitted calls succeed and hold their slot for a
# measurable window. The question here is capacity, not failure.
set_upstream '{"mode":"Slow","latencyMs":600}'

step "12 concurrent calls against a bulkhead of 2 permits + 1 queue slot"
PIDS=""
for i in $(seq 1 12); do call_read "b$i.json" >/dev/null & PIDS="$PIDS $!"; done

# Wait on those twelve specifically, not a bare "wait". A bare wait also waits on the API
# process started earlier in this same shell, which never exits - the script hangs here forever
# and looks like a deadlock in the bulkhead rather than a bug in the harness.
for pid in $PIDS; do wait "$pid"; done

ADMITTED=0; SHED=0
for i in $(seq 1 12); do
  case "$(field "b$i.json" outcome)" in
    Succeeded)        ADMITTED=$((ADMITTED+1)) ;;
    BulkheadRejected) SHED=$((SHED+1)) ;;
  esac
done

info "admitted: $ADMITTED    shed: $SHED"
info "bulkheadRejections counter: $(stats_field bulkheadRejections)"
for i in $(seq 1 12); do
  if [ "$(field "b$i.json" outcome)" = "BulkheadRejected" ]; then
    info "sample rejection: $(field "b$i.json" detail) ($(field "b$i.json" elapsedMs)ms)"
    break
  fi
done

[ "$SHED" -gt 0 ] \
  && ok "$SHED callers were shed fast instead of all 12 queueing behind a slow dependency" \
  || no "nothing was shed; the bulkhead admitted every call"

[ "$ADMITTED" -le 3 ] \
  && ok "at most permits+queue (3) were admitted at once" \
  || no "$ADMITTED admitted, which is more than the bulkhead should allow"

[ "$(circuit)" = "Closed" ] \
  && ok "bulkhead rejections never reached the breaker - shedding is not a dependency failure" \
  || no "circuit is $(circuit); shed load was miscounted as dependency failure"

# =============================================================================================
section "Summary"

set_upstream '{"mode":"None"}'
fetch stats.json "$BASE/api/resilience/stats" >/dev/null
pretty stats.json

printf '\n  %s passed, %s failed\n\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ]
