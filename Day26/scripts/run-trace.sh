#!/usr/bin/env bash
#
# Day 26 — run both roles, generate traffic, and let the trace flow to App Insights.
#
#   ./scripts/run-trace.sh [quote-count]
#
# Starts two processes from ONE binary:
#
#   SERVICE_ROLE=api      HTTP endpoints. Writes quotes and outbox rows. Publishes nothing.
#   SERVICE_ROLE=worker   No endpoints. Outbox relay + subscription consumers.
#
# Then creates quotes over HTTP and waits for the outbox to drain, so every trace is complete
# before the script exits. Telemetry keeps arriving for a minute or two afterwards — the exporter
# batches, and Log Analytics ingestion is not instant.
set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.." || exit 1

QUOTE_COUNT="${1:-12}"
API_PORT="${API_PORT:-5310}"
BACKEND="$(pwd)/backend"

die() { printf '\n%s\n' "$*" >&2; exit 1; }

[ -f .env ] || die "No .env. Run ./scripts/deploy.sh first."

# ---------------------------------------------------------------------------------------------
# Load the deployment's own settings.
#
# `set -a` exports everything assigned until `set +a`, which is what makes these visible to the
# child dotnet processes without naming each one twice.
# ---------------------------------------------------------------------------------------------
set -a
# shellcheck disable=SC1091
source .env
set +a

[ -n "${APPLICATIONINSIGHTS_CONNECTION_STRING:-}" ] || die ".env has no App Insights connection string."
[ -n "${ServiceBus__FullyQualifiedNamespace:-}" ] || die ".env has no Service Bus namespace."

# ---------------------------------------------------------------------------------------------
# Runtime-only credentials, generated per run and never written to disk.
#
# The API needs a JWT signing key and a seed user to exist before a quote can be created. Both
# are invented here: the key is random per run, and the address uses the reserved `.invalid` TLD
# so it can never resolve to or collide with a real mailbox.
#
# Generated rather than committed because a signing key in a repository is a signing key in
# everybody's repository, and because this codebase spent Day 25 establishing that the fix for a
# credential is to not have one that outlives its use.
# ---------------------------------------------------------------------------------------------
export Jwt__Key="$(python -c "import secrets;print(secrets.token_urlsafe(48))")"
export Seed__AdminEmail="seed-admin@example.invalid"
export Seed__AdminPassword="$(python -c "import secrets;print(secrets.token_urlsafe(24))")"

# ---------------------------------------------------------------------------------------------
# A fresh database.
#
# The application uses EnsureCreated rather than migrations, so it creates the schema from the
# current model and NEVER alters an existing file. Day 26 added TraceParent and TraceState to the
# outbox table; against a database created before today those columns simply would not exist, and
# the failure is an EF error naming a column rather than anything about tracing.
#
# The -wal and -shm files go too. They are the write-ahead log; leaving them beside a deleted
# database is how SQLite ends up reading state that no longer matches the file it belongs to.
# ---------------------------------------------------------------------------------------------
# A process left over from an earlier run holds the database open, and on Windows an open file
# cannot be deleted. Clear them BEFORE trying, not only in the exit trap - the trap protects the
# next run only if this run reaches it, and a run that dies on a failed assertion does not.
# ---------------------------------------------------------------------------------------------
# Stopping the app processes, reliably.
#
# `taskkill //F //IM QuotesApi.exe` works when typed at a Git Bash prompt and silently failed
# when run from inside this script, leaving processes alive across runs and the database locked.
# Rather than keep guessing at why the argument mangling differs, this goes through PowerShell,
# which needs no MSYS argument translation at all and reports how many processes remain.
#
# It also WAITS. Stop-Process returns once the terminate is signalled, not once the process has
# exited and Windows has released its file handles.
# ---------------------------------------------------------------------------------------------
stop_app_processes() {
  powershell -NoProfile -Command "
    Get-Process QuotesApi -ErrorAction SilentlyContinue | Stop-Process -Force
    for (\$i = 0; \$i -lt 20; \$i++) {
      if (-not (Get-Process QuotesApi -ErrorAction SilentlyContinue)) { break }
      Start-Sleep -Milliseconds 500
    }
  " >/dev/null 2>&1
}

# Kill, then WAIT for the processes to actually be gone.
#
# `taskkill` returns as soon as the terminate is signalled, not once the process has exited and
# Windows has released its file handles. Treating it as synchronous is why the first two attempts
# at this failed: the kill "succeeded", the delete ran immediately afterwards, and the file was
# still held. Polling tasklist until the image disappears is the difference between a script that
# works and one that works most of the time.
echo "Stopping any QuotesApi process left over from an earlier run..."
stop_app_processes

# Even once the process is gone the handle release can lag by a moment, so the delete retries.
for _ in $(seq 1 10); do
  rm -f "$BACKEND"/quotes.db "$BACKEND"/quotes.db-wal "$BACKEND"/quotes.db-shm 2>/dev/null
  [ -f "$BACKEND/quotes.db" ] || break
  sleep 1
done

# VERIFY, rather than announce.
#
# The first version printed "Removed any existing database" unconditionally, and on a run where
# the file was locked it printed that while the old database survived. The consequence was not
# obvious: the seed user already existed, so seeding was skipped, so the freshly generated
# password did not match the stored hash, and login failed with 401 - an error that points at
# authentication and was caused by a delete that silently did nothing.
if [ -f "$BACKEND/quotes.db" ]; then
  die "Could not delete $BACKEND/quotes.db - something still has it open.
Close any running QuotesApi process and retry. Proceeding would reuse a database seeded with a
DIFFERENT random password, and login would fail with a 401 that has nothing to do with auth."
fi
echo "Database removed; the api role will recreate it with the new outbox columns."

mkdir -p docs logs

# Scratch space for response bodies, so a failure can be shown rather than just counted.
RUN_TMP=".run-tmp"
mkdir -p "$RUN_TMP"
API_LOG="logs/api.log"
WORKER_LOG="logs/worker.log"

cleanup() {
  echo
  echo "Stopping..."

  # By IMAGE NAME, not by the PID this script captured.
  #
  # `dotnet run` builds, then launches the application as a CHILD process. The PID recorded from
  # a backgrounded subshell is the subshell, and killing it leaves the actual QuotesApi.exe
  # running - still holding the build output, the SQLite file and port 5310, so the next run
  # fails with "the file is locked by QuotesApi" and a health check that never succeeds.
  #
  # Day 20 lost time to precisely this against `dotnet exec`. //T takes the process tree, which
  # covers the subshell and the dotnet host together.
  stop_app_processes
  rm -rf "${RUN_TMP:-.run-tmp}"
}
trap cleanup EXIT

# ---------------------------------------------------------------------------------------------
# Build ONCE, then run both roles with --no-build.
#
# Two concurrent `dotnet run` invocations both try to compile into the same obj/ and bin/, and
# the loser fails with:
#
#   MSB3021: Unable to copy ... QuotesApi.exe ... being used by another process
#
# The race is not the interesting part of this exercise, so it is removed rather than worked
# around: one build, then two processes that only launch.
# ---------------------------------------------------------------------------------------------
echo "Building once, so the two roles do not race on obj/..."
( cd "$BACKEND" && dotnet build --nologo -v q ) >/dev/null 2>&1 || die "Build failed."

# ---------------------------------------------------------------------------------------------
# Start the API FIRST, because it owns the schema.
#
# EnsureCreated is not atomic: its existence check and its CREATE TABLE are separate statements,
# and two processes starting together land in the window between them reliably rather than
# occasionally. The first attempt at this script started the worker first and both roles raced:
#
#   SQLite Error 1: 'table "OutboxMessages" already exists'
#
# Program.cs now gives schema creation and seeding to the api role alone, and the worker polls
# until the tables appear. Starting the API first simply means the worker never has to wait.
# ---------------------------------------------------------------------------------------------
echo "Starting the api role on port ${API_PORT}..."
(
  cd "$BACKEND" || exit 1
  SERVICE_ROLE=api   ASPNETCORE_URLS="http://127.0.0.1:${API_PORT}"   dotnet run --no-build --no-launch-profile
) > "$API_LOG" 2>&1 &
API_PID=$!

echo "Starting the worker role..."
(
  cd "$BACKEND" || exit 1
  SERVICE_ROLE=worker   ASPNETCORE_URLS="http://127.0.0.1:0"   dotnet run --no-build --no-launch-profile
) > "$WORKER_LOG" 2>&1 &
WORKER_PID=$!

# ---- wait for the API ------------------------------------------------------------------------
echo -n "Waiting for the API"
for _ in $(seq 1 60); do
  if curl -fsS --max-time 3 "http://127.0.0.1:${API_PORT}/health" >/dev/null 2>&1; then
    echo " — up."
    READY=1
    break
  fi
  echo -n "."
  sleep 2
done
[ "${READY:-0}" = "1" ] || { echo; tail -30 "$API_LOG"; die "The API never became healthy."; }

# ---- wait for the worker's consumers to attach -----------------------------------------------
echo -n "Waiting for the worker consumers"
for _ in $(seq 1 45); do
  if grep -q "competing consumers running" "$WORKER_LOG" 2>/dev/null; then
    echo " — attached."
    WORKER_READY=1
    break
  fi
  if grep -qi "Unhandled exception\|AuthorizationFailed\|Unauthorized" "$WORKER_LOG" 2>/dev/null; then
    echo; tail -30 "$WORKER_LOG"; die "The worker failed to start."
  fi
  echo -n "."
  sleep 2
done
[ "${WORKER_READY:-0}" = "1" ] || { echo; tail -30 "$WORKER_LOG"; die "Worker consumers never attached."; }

# ---- authenticate ----------------------------------------------------------------------------
echo
echo "Signing in as the seeded user..."
TOKEN="$(curl -fsS --max-time 20 -X POST "http://127.0.0.1:${API_PORT}/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d "{\"email\":\"${Seed__AdminEmail}\",\"password\":\"${Seed__AdminPassword}\"}" \
  | python -c "import json,sys;print(json.load(sys.stdin).get('accessToken') or json.load(sys.stdin).get('token',''))" 2>/dev/null)"

[ -n "$TOKEN" ] || {
  # Some builds return the token under a different key; show the shape rather than guessing again.
  echo "Login response did not contain a recognised token field:"
  curl -sS --max-time 20 -X POST "http://127.0.0.1:${API_PORT}/api/auth/login" \
    -H 'Content-Type: application/json' \
    -d "{\"email\":\"${Seed__AdminEmail}\",\"password\":\"${Seed__AdminPassword}\"}" | head -c 400
  echo
  die "Could not obtain an access token."
}
echo "  token acquired (${#TOKEN} chars)."

# ---------------------------------------------------------------------------------------------
# Generate traffic.
#
# A deliberate mix, because a workload of one shape produces a percentile table with one row and
# proves nothing about the query:
#
#   POST /api/quotes    writes, and the ONLY path that produces an outbox row -> the traced chain
#   GET  /api/quotes    reads, served from cache after the first, so p50 and p99 differ visibly
#   GET  /health        cheap, and excluded from the latency queries by name
#   GET  /api/quotes/0  a deliberate 404, so the error-rate query has something to find
# ---------------------------------------------------------------------------------------------
echo
echo "Generating traffic: ${QUOTE_COUNT} quotes, plus reads and a few failures..."

AUTHORS=("Ada Lovelace" "Grace Hopper" "Alan Turing" "Barbara Liskov" "Edsger W. Dijkstra")

for i in $(seq 1 "$QUOTE_COUNT"); do
  AUTHOR="${AUTHORS[$(( (i - 1) % ${#AUTHORS[@]} ))]}"
  CODE="$(curl -sS --max-time 30 -o "$RUN_TMP/post.out" -w '%{http_code}' \
    -X POST "http://127.0.0.1:${API_PORT}/api/quotes" \
    -H "Authorization: Bearer ${TOKEN}" \
    -H 'Content-Type: application/json' \
    -d "{\"author\":\"${AUTHOR}\",\"text\":\"Observability run ${i} - a trace is only useful if it survives every hop.\"}" \
    2>/dev/null)"

  if [ "$CODE" = "201" ] || [ "$CODE" = "200" ]; then
    printf 'w'
  else
    printf 'W'
    # Show the FIRST failure in full, once. A row of W characters says something broke and
    # nothing about what, and re-running to find out costs another two minutes.
    if [ -z "${FIRST_FAILURE_SHOWN:-}" ]; then
      FIRST_FAILURE_SHOWN=1
      echo
      echo "  first POST failure: HTTP ${CODE}"
      head -c 400 "$RUN_TMP/post.out" 2>/dev/null | sed 's/^/    /'
      echo
    fi
  fi

  curl -fsS --max-time 20 "http://127.0.0.1:${API_PORT}/api/quotes" \
    -H "Authorization: Bearer ${TOKEN}" >/dev/null 2>&1 && printf 'r' || printf 'R'

  # One failure every fourth iteration. The error-rate alert needs a non-zero rate to be
  # demonstrable, and a rate of exactly zero proves only that nothing was measured.
  if [ $(( i % 4 )) -eq 0 ]; then
    curl -fsS --max-time 20 "http://127.0.0.1:${API_PORT}/api/quotes/999999" \
      -H "Authorization: Bearer ${TOKEN}" >/dev/null 2>&1 && printf 'x' || printf 'e'
  fi
done
echo
echo "  lowercase = expected outcome, uppercase = unexpected."

# ---- wait for the outbox to drain ------------------------------------------------------------
# The trace is not complete until the relay has published every row and the consumers have run.
# Exiting before then captures half a picture and blames the propagation code for it.
echo
echo -n "Waiting for the outbox to drain"
for _ in $(seq 1 60); do
  # GET /api/outbox returns { pending, processed, total, recent }. Read `pending` directly.
  #
  # The first version of this guessed at the shape — it looked for a `messages` or `items` array,
  # found neither, fell back to an empty list, and summed zero. So it announced "drained" on the
  # very first check while twelve rows were still unpublished, and the script then killed the
  # worker six seconds after it had claimed them. The relay was working; the CHECK was not.
  #
  # A missing key now yields '?' rather than 0, so "cannot tell" can never be mistaken for
  # "nothing left to do".
  PENDING="$(curl -fsS --max-time 10 "http://127.0.0.1:${API_PORT}/api/outbox"     -H "Authorization: Bearer ${TOKEN}" 2>/dev/null     | python -c "
import json,sys
try:
    d = json.load(sys.stdin)
    print(d['pending'] if isinstance(d, dict) and 'pending' in d else '?')
except Exception:
    print('?')
" 2>/dev/null)"

  if [ "$PENDING" = "0" ]; then
    echo " — drained."
    DRAINED=1
    break
  fi
  echo -n "."
  sleep 2
done

[ "${DRAINED:-0}" = "1" ] || echo " — still pending (${PENDING:-?}); see logs/worker.log."

# ---------------------------------------------------------------------------------------------
# LET THE EXPORTER FLUSH before anything is killed.
#
# This is not padding. OpenTelemetry batches spans in memory and exports them on a timer -
# BatchExportProcessor's default schedule is every 5 seconds - and the cleanup below terminates
# the processes with Stop-Process -Force, which is a hard kill with no graceful shutdown and no
# opportunity to flush.
#
# The first run that got this far produced a complete, correct trace chain in the logs (12 rows
# published, 24 messages consumed) and ZERO telemetry in App Insights. Both roles reported
# `azureMonitor=enabled` at startup, so the exporter was configured correctly; the spans simply
# never left the process. `requests | count` returned 0.
#
# Sixty seconds covers the trace batch several times over and one full metrics export interval,
# which defaults to 60s and is the slower of the two.
# ---------------------------------------------------------------------------------------------
echo
echo -n "Letting the OpenTelemetry exporter flush (60s - a hard kill loses whatever is still batched)"
for _ in $(seq 1 12); do
  sleep 5
  echo -n "."
done
echo " done."

PUBLISHED="$(grep -c "Published outbox message" "$WORKER_LOG" 2>/dev/null)"
CONSUMED="$(grep -c "completed MessageId" "$WORKER_LOG" 2>/dev/null)"

cat <<EOF

=============================================================================================
 Traffic complete
=============================================================================================

  quotes created        ${QUOTE_COUNT}
  outbox rows published ${PUBLISHED}     (worker role)
  messages consumed     ${CONSUMED}     (2 subscriptions x each message)

  logs                  ${API_LOG}, ${WORKER_LOG}

Telemetry is batched by the exporter and then ingested by Log Analytics, so allow 2-3 minutes
before querying. Then:

  ./scripts/query-kql.sh

EOF
