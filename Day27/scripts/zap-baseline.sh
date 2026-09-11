#!/usr/bin/env bash
#
# Day 27 — OWASP ZAP baseline scan against the running API.
#
#   ./scripts/zap-baseline.sh before      # scan the unhardened app
#   ./scripts/zap-baseline.sh after       # scan it again once hardened
#
# Starts the API, points ZAP at it, writes docs/zap-<label>.{html,json,txt}, stops the API.
#
# ---------------------------------------------------------------------------------------------
# WHAT A "BASELINE" SCAN IS, AND WHAT IT IS NOT
#
# ZAP's baseline scan is PASSIVE. It spiders the app and inspects the traffic it sees; it does not
# attack. No injection payloads, no fuzzing, no attempt to exploit anything.
#
# That makes it fast and safe to run in CI, and it also means a clean baseline proves very little
# about application logic. It finds missing security headers, cookie flags, information leaks in
# banners and error pages - the whole class of problem that is invisible in code review because
# it is about what the server does NOT say.
#
# Authorisation bugs, injection, broken access control: a baseline scan finds none of those. The
# threat model is what covers them, which is why both are in today rather than either alone.
# ---------------------------------------------------------------------------------------------
set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.." || exit 1

LABEL="${1:-before}"
API_PORT="${API_PORT:-5320}"

# ---------------------------------------------------------------------------------------------
# WHICH CODE GETS SCANNED
#
# "before" scans Day26/backend and "after" scans Day27/backend, and that is not a shortcut - it
# is the only way to get an honest comparison now that the hardening is written.
#
# Day27/backend STARTED as a byte-for-byte copy of Day26/backend; the security pass is the whole
# difference between them. Scanning Day 26 therefore scans exactly this application as it was
# before today, and a second scan of Day 27 isolates what the pass changed.
#
# The alternative - reverting the hardening, scanning, then re-applying it - produces the same
# numbers and risks the revert not being faithful.
# ---------------------------------------------------------------------------------------------
if [ "$LABEL" = "before" ]; then
  BACKEND="$(cd .. && pwd)/Day26/backend"
  ENVIRONMENT="Production"
else
  BACKEND="$(pwd)/backend"
  ENVIRONMENT="Production"
fi
echo "Scanning: ${BACKEND}  (ASPNETCORE_ENVIRONMENT=${ENVIRONMENT})"

die() { printf '\n%s\n' "$*" >&2; exit 1; }

docker info >/dev/null 2>&1 || die "Docker is not running. ZAP runs in a container."

mkdir -p docs zap logs

# Same lesson as Day 26: taskkill is unreliable from this shell, and Stop-Process returns before
# the handles are released.
stop_app() {
  powershell -NoProfile -Command "
    Get-Process QuotesApi -ErrorAction SilentlyContinue | Stop-Process -Force
    for (\$i = 0; \$i -lt 20; \$i++) {
      if (-not (Get-Process QuotesApi -ErrorAction SilentlyContinue)) { break }
      Start-Sleep -Milliseconds 500
    }
  " >/dev/null 2>&1
}
trap stop_app EXIT

stop_app
for _ in $(seq 1 10); do
  rm -f "$BACKEND"/quotes.db "$BACKEND"/quotes.db-wal "$BACKEND"/quotes.db-shm 2>/dev/null
  [ -f "$BACKEND/quotes.db" ] || break
  sleep 1
done

# ---------------------------------------------------------------------------------------------
# Run with NO Azure configuration at all.
#
# No App Insights, no Service Bus, no Redis. The API degrades to a no-op publisher and an L1-only
# cache, which is exactly what is wanted: the scan is of the HTTP surface, and a scan that also
# depends on a broker being reachable fails for reasons that have nothing to do with security.
# ---------------------------------------------------------------------------------------------
export Jwt__Key="$(python -c "import secrets;print(secrets.token_urlsafe(48))")"
export Seed__AdminEmail="seed-admin@example.invalid"
export Seed__AdminPassword="$(python -c "import secrets;print(secrets.token_urlsafe(24))")"

echo "Building..."
( cd "$BACKEND" && dotnet build --nologo -v q ) >/dev/null 2>&1 || die "Build failed."

echo "Starting the API on ${API_PORT}..."
(
  cd "$BACKEND" || exit 1
  SERVICE_ROLE=api ASPNETCORE_ENVIRONMENT="${ENVIRONMENT}" ASPNETCORE_URLS="http://0.0.0.0:${API_PORT}" dotnet run --no-build --no-launch-profile
) > "logs/zap-api-${LABEL}.log" 2>&1 &

echo -n "Waiting for /health"
for _ in $(seq 1 45); do
  curl -fsS --max-time 3 "http://127.0.0.1:${API_PORT}/health" >/dev/null 2>&1 && { echo " — up."; READY=1; break; }
  echo -n "."
  sleep 2
done
[ "${READY:-0}" = "1" ] || { echo; tail -25 "logs/zap-api-${LABEL}.log"; die "API never became healthy."; }

# ---------------------------------------------------------------------------------------------
# Bind to 0.0.0.0, reach it as host.docker.internal.
#
# The API listens on all interfaces rather than 127.0.0.1 because the scanner is in a container
# and 127.0.0.1 inside that container is the container itself. Docker Desktop resolves
# host.docker.internal to the host, which is the documented way across the boundary.
# ---------------------------------------------------------------------------------------------
# ---------------------------------------------------------------------------------------------
# Target /health, not /.
#
# This is a JSON API with no root document and no HTML anywhere, so a spider pointed at / gets a
# 404 and has no links to follow. The first run did exactly that and reported "PASS: 66" from a
# single 404 response — technically true and worth nothing, because the passive rules had almost
# no traffic to inspect.
#
# /health returns 200 in both the before and after builds and is anonymous in both, so the two
# scans see a comparable response. The passive rules that matter here — the missing-header family
# — apply to any response, so one real 200 is enough to exercise them.
#
# The honest limit: this scans ONE endpoint's headers. It does not walk the API surface, because
# a baseline scan cannot authenticate. Covering the authenticated surface needs zap-api-scan.py
# driven by the OpenAPI document, which is noted as a gap in EXERCISE.md.
# ---------------------------------------------------------------------------------------------
TARGET="http://host.docker.internal:${API_PORT}/health"
echo
echo "=== ZAP baseline against ${TARGET} ==="

# -I  do not fail the build on warnings. This script reports; the decision about which findings
#     matter is made by a human reading them, and an exit code cannot express "accepted risk".
# -j  use the AJAX spider too - it is a JSON API with no HTML, so this mostly confirms there is
#     no hidden client-side surface.
docker run --rm \
  -v "$(pwd -W 2>/dev/null || pwd)/zap:/zap/wrk:rw" \
  --add-host=host.docker.internal:host-gateway \
  ghcr.io/zaproxy/zaproxy:stable \
  zap-baseline.py \
    -t "$TARGET" \
    -I \
    -r "zap-${LABEL}.html" \
    -J "zap-${LABEL}.json" \
  2>&1 | tee "docs/zap-${LABEL}.txt"

echo
echo "Reports:"
echo "  docs/zap-${LABEL}.txt      the console summary"
echo "  zap/zap-${LABEL}.html      the full report"
echo "  zap/zap-${LABEL}.json      machine-readable"

# ---- a compact summary for the write-up -------------------------------------------------------
python - "docs/zap-${LABEL}.txt" <<'PY'
import re, sys, io
text = io.open(sys.argv[1], encoding='utf-8', errors='replace').read()
counts = dict(re.findall(r'(FAIL-NEW|FAIL-INPROG|WARN-NEW|WARN-INPROG|INFO|IGNORE|PASS):\s*(\d+)', text))
print()
print('  ' + '  '.join(f'{k}={v}' for k, v in counts.items()))
alerts = sorted(set(re.findall(r'^(?:WARN|FAIL)-(?:NEW|INPROG):\s*(.+?)\s*\[\d+\]', text, re.M)))
print(f'  {len(alerts)} distinct alert(s):')
for a in alerts:
    print(f'    - {a}')
PY
