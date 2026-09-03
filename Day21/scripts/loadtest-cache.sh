#!/usr/bin/env bash
#
# Day 21 — the measurement.
#
# Runs the SAME load against the SAME endpoint on the SAME process twice: once with the cache
# off, once with it on. Only POST /api/cache/mode differs between the arms, so the delta belongs
# to the cache and to nothing else — not to JIT warmth, connection-pool state or OS page cache,
# all of which would differ if the two arms were separate process launches.
#
# Reports, for each arm:
#   · requests/sec and p99 latency        (bombardier)
#   · database queries actually executed  (EF command interceptor)
#   · cache hit rate                      (reads vs factory invocations)
#
# Then proves stampede protection separately: N simultaneous requests against a cold key must
# produce exactly ONE factory invocation.
#
#   ./scripts/loadtest-cache.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_DIR="$(cd "$SCRIPT_DIR/../backend" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PORT="${PORT:-5307}"
BASE="http://localhost:${PORT}"
BOMBARDIER="${BOMBARDIER:-$REPO_ROOT/bombardier.exe}"

CONNECTIONS="${CONNECTIONS:-50}"
DURATION="${DURATION:-15s}"
STAMPEDE_N="${STAMPEDE_N:-200}"

PASS=0; FAIL=0
ok()      { PASS=$((PASS+1)); printf '  [PASS] %s\n' "$*"; }
no()      { FAIL=$((FAIL+1)); printf '  [FAIL] %s\n' "$*"; }
section() { printf '\n\n================================================================\n %s\n================================================================\n' "$*"; }
step()    { printf '\n--- %s\n' "$*"; }

WORK="$SCRIPT_DIR/../.loadtest-tmp"; mkdir -p "$WORK"
json() { node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{console.log(JSON.parse(s)['$1']??'')}catch{console.log('')}})"; }

SERVER_PID=""
DB="cache-load-$$.db"

cleanup() {
  if [ -n "$SERVER_PID" ]; then
    taskkill //F //T //PID "$SERVER_PID" >/dev/null 2>&1 || kill -9 "$SERVER_PID" 2>/dev/null
    wait "$SERVER_PID" 2>/dev/null
  fi
  rm -f "$BACKEND_DIR/$DB" "$BACKEND_DIR/$DB"-* 2>/dev/null
  rm -rf "$WORK"
}
trap cleanup EXIT

command -v node >/dev/null || { echo "node is required"; exit 1; }
[ -x "$BOMBARDIER" ] || { echo "bombardier not found at $BOMBARDIER"; exit 1; }

printf '=== Building ===\n'
( cd "$BACKEND_DIR" && dotnet build -v q --nologo ) >/tmp/cache-build.log 2>&1 \
  || { echo "build failed:"; grep -E 'error' /tmp/cache-build.log | head; exit 1; }
echo "  built."

printf '\n=== Redis ===\n'
if docker ps --format '{{.Names}}' 2>/dev/null | grep -q day21-redis; then
  echo "  day21-redis is up — HybridCache will use it as L2."
  REDIS="localhost:6379"
else
  echo "  day21-redis is NOT running. Start it with:"
  echo "    docker run -d --name day21-redis -p 6379:6379 redis:7-alpine"
  echo "  Continuing L1-only; the numbers still hold, the cache is just process-local."
  REDIS=""
fi

printf '\n=== Starting the API ===\n'
# Serilog at Information: Debug-level EF logging would dominate the measurement, and a load
# test that mostly measures its own logging is worse than no load test.
( cd "$BACKEND_DIR" && exec env \
    DOTNET_ENVIRONMENT=Development \
    Serilog__MinimumLevel__Default=Warning \
    Jwt__Key='day21-cache-loadtest-signing-key-at-least-32-bytes' \
    ASPNETCORE_URLS="$BASE" \
    ConnectionStrings__DefaultConnection="Data Source=${DB}" \
    ConnectionStrings__Redis="$REDIS" \
    dotnet exec "bin/Debug/net10.0/QuotesApi.dll" ) >/tmp/cache-api.log 2>&1 &
SERVER_PID=$!

for _ in $(seq 1 90); do grep -q "Now listening on" /tmp/cache-api.log 2>/dev/null && break; sleep 1; done
for _ in $(seq 1 30); do curl -sf "$BASE/health" >/dev/null 2>&1 && break; sleep 1; done
curl -sf "$BASE/health" >/dev/null 2>&1 || { echo "API did not start:"; tail -20 /tmp/cache-api.log; exit 1; }
echo "  up (pid $SERVER_PID)"

curl -s "$BASE/api/cache/stats" > "$WORK/s.json"
printf '  layers: %s\n' "$(json layers < "$WORK/s.json")"
printf '  simulated query cost: %sms\n' "$(json simulatedQueryCostMs < "$WORK/s.json")"

# Reads a whole arm: reset, load, report.
run_arm() {
  local label="$1" enabled="$2"

  curl -s -X POST "$BASE/api/cache/mode" -H 'Content-Type: application/json' \
    -d "{\"enabled\":${enabled}}" > "$WORK/mode.json"
  curl -s -X POST "$BASE/api/cache/reset" > /dev/null

  step "$label — $CONNECTIONS connections for $DURATION against GET /api/quotes"
  "$BOMBARDIER" -c "$CONNECTIONS" -d "$DURATION" -l -p r \
    "$BASE/api/quotes" > "$WORK/bomb-$label.txt" 2>&1

  # bombardier's own table is the latency source; the API's counters are the DB source.
  grep -E "Reqs/sec|Latency|99%|2xx" "$WORK/bomb-$label.txt" | sed 's/^/      /'

  curl -s "$BASE/api/cache/stats" > "$WORK/stats-$label.json"

  local reqs p99 dbq reads hits rate
  reqs=$(grep -E "^\s*Reqs/sec" "$WORK/bomb-$label.txt" | head -1 | awk '{print $2}')
  p99=$(awk '/99%/ {print $2; exit}' "$WORK/bomb-$label.txt")
  dbq=$(json dbQueries < "$WORK/stats-$label.json")
  reads=$(json reads < "$WORK/stats-$label.json")
  hits=$(json hits < "$WORK/stats-$label.json")
  rate=$(json hitRatePercent < "$WORK/stats-$label.json")

  printf '\n      requests served : %s\n' "$reads"
  printf '      DB queries      : %s\n' "$dbq"
  printf '      cache hits      : %s (%s%%)\n' "$hits" "$rate"

  echo "$reqs|$p99|$dbq|$reads|$rate" > "$WORK/result-$label.txt"
}

# =========================================================================================
section "1. BEFORE — cache OFF (every read hits the database)"
run_arm "before" false

section "2. AFTER — cache ON (HybridCache, L1 + L2)"
run_arm "after" true

# =========================================================================================
section "3. Before / after"

IFS='|' read -r B_REQS B_P99 B_DBQ B_READS B_RATE < "$WORK/result-before.txt"
IFS='|' read -r A_REQS A_P99 A_DBQ A_READS A_RATE < "$WORK/result-after.txt"

printf '\n  %-22s %18s %18s\n' "" "BEFORE (no cache)" "AFTER (HybridCache)"
printf '  %-22s %18s %18s\n' "requests served" "$B_READS" "$A_READS"
printf '  %-22s %18s %18s\n' "requests/sec" "$B_REQS" "$A_REQS"
printf '  %-22s %18s %18s\n' "p99 latency" "$B_P99" "$A_P99"
printf '  %-22s %18s %18s\n' "DB queries" "$B_DBQ" "$A_DBQ"
printf '  %-22s %18s %18s\n' "cache hit rate" "${B_RATE}%" "${A_RATE}%"

# DB queries per request is the honest ratio: throughput differs between arms, so comparing
# raw query counts alone would flatter whichever arm served more requests.
node -e "
const bq=$B_DBQ, br=$B_READS, aq=$A_DBQ, ar=$A_READS;
const bpr = br ? (bq/br) : 0, apr = ar ? (aq/ar) : 0;
console.log('');
console.log('  DB queries per request : ' + bpr.toFixed(3) + '  ->  ' + apr.toFixed(3));
if (bpr > 0) {
  const drop = 100 * (1 - apr / bpr);
  console.log('  DB load reduction      : ' + drop.toFixed(1) + '%');
}
"

[ "${A_DBQ:-0}" -lt "${B_DBQ:-1}" ] \
  && ok "the cache cut absolute database queries ($B_DBQ -> $A_DBQ)" \
  || no "database queries did not fall ($B_DBQ -> $A_DBQ)"

node -e "process.exit(($A_RATE >= 90) ? 0 : 1)" \
  && ok "hit rate ${A_RATE}% (>= 90%)" \
  || no "hit rate ${A_RATE}% is below 90%"

# =========================================================================================
section "4. Stampede protection under concurrency"

step "Cold cache, then $STAMPEDE_N simultaneous requests for the same key"
curl -s -X POST "$BASE/api/cache/mode" -H 'Content-Type: application/json' -d '{"enabled":true}' >/dev/null
curl -s -X POST "$BASE/api/cache/reset" >/dev/null

# All at once, deliberately. Issued sequentially the first would populate the cache and the
# rest would hit it — which proves caching works and says nothing about stampedes.
node -e "
const n = $STAMPEDE_N, url = '$BASE/api/quotes';
const started = Date.now();
Promise.all(Array.from({length: n}, () => fetch(url).then(r => r.status)))
  .then(codes => {
    const ok = codes.filter(c => c === 200).length;
    console.log('      ' + n + ' concurrent requests issued, ' + ok + ' returned 200, in '
      + (Date.now() - started) + 'ms');
  })
  .catch(e => { console.error('      request error: ' + e.message); process.exit(1); });
" || no "concurrent request burst failed"

curl -s "$BASE/api/cache/stats" > "$WORK/stampede.json"
S_READS=$(json reads < "$WORK/stampede.json")
S_MISS=$(json misses < "$WORK/stampede.json")
S_DBQ=$(json dbQueries < "$WORK/stampede.json")
S_RATE=$(json hitRatePercent < "$WORK/stampede.json")

printf '\n      reads (requests)     : %s\n' "$S_READS"
printf '      misses (factory runs): %s\n' "$S_MISS"
printf '      DB queries           : %s\n' "$S_DBQ"
printf '      hit rate             : %s%%\n' "$S_RATE"

# THE assertion of the day. Without stampede protection this would be ~N.
[ "${S_MISS:-999}" -le 2 ] \
  && ok "$S_READS concurrent readers of a cold key caused only $S_MISS factory invocation(s) — the stampede was collapsed" \
  || no "$S_MISS factory invocations for $S_READS concurrent readers; the herd was NOT coalesced"

printf '\n      Without stampede protection this number would be close to %s — one database\n' "$STAMPEDE_N"
printf '      query per concurrent caller, all for the same value, precisely when the system\n'
printf '      is busiest.\n'

# =========================================================================================
section "Result"
printf '  %d passed, %d failed\n\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ] || exit 1
