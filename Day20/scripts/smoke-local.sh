#!/usr/bin/env bash
#
# Local smoke test for the caller-identity middleware — no Azure involved.
#
# It cannot prove a real managed-identity token is accepted; that needs Entra, and
# verify.sh does it against the deployed system. What it does prove, on a laptop, is the
# half that is easy to get wrong and expensive to discover in production:
#
#   · enforcement OFF  -> the Week-1 API behaves exactly as it always did
#   · enforcement ON   -> /api/* is refused without a caller token
#   · enforcement ON   -> /health still answers, so the platform probe survives
#
# That last one is the one that bites. A middleware scoped one path segment too wide takes
# the health endpoint down with it, Container Apps marks every revision unhealthy, and the
# deployment fails with no mention of authentication anywhere in the logs.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_DIR="$(cd "$SCRIPT_DIR/../backend" && pwd)"
PORT="${PORT:-5267}"
BASE="http://localhost:${PORT}"
DLL="$BACKEND_DIR/bin/Debug/net10.0/QuotesApi.dll"

PASS=0; FAIL=0
ok() { PASS=$((PASS+1)); printf '  [PASS] %s\n' "$*"; }
no() { FAIL=$((FAIL+1)); printf '  [FAIL] %s\n' "$*"; }

SERVER_PID=""
PHASE=0

stop_api() {
  [ -n "$SERVER_PID" ] || return 0
  # `dotnet run` launches the app as a *child* of the build host, so killing the shell job
  # leaves the real listener alive — the next phase then silently tests the previous
  # configuration and every assertion about it is meaningless. Running the built DLL through
  # `dotnet exec` gives one process, and //T still cleans up any child on Windows.
  kill "$SERVER_PID" 2>/dev/null
  taskkill //F //T //PID "$SERVER_PID" >/dev/null 2>&1
  wait "$SERVER_PID" 2>/dev/null
  SERVER_PID=""
  # SQLite keeps the file handle open a moment past process exit on Windows.
  for _ in $(seq 1 20); do
    curl -sf -o /dev/null "$BASE/health" 2>/dev/null || break
    sleep 0.5
  done
}

cleanup() { stop_api; rm -f "$BACKEND_DIR"/smoke-test-*.db; }
trap cleanup EXIT

start_api() {
  stop_api
  PHASE=$((PHASE+1))
  local db="smoke-test-$$-${PHASE}.db"

  # A relative filename on purpose. InfrastructureExtensions resolves relative paths against
  # the content root; an absolute Git Bash path like /tmp/x.db is not something Windows .NET
  # can open, and it fails as "SQLite Error 14: unable to open database file".
  ( cd "$BACKEND_DIR" && exec env "$@" \
      Jwt__Key='local-smoke-test-signing-key-at-least-32-bytes-long' \
      ASPNETCORE_URLS="$BASE" \
      ConnectionStrings__DefaultConnection="Data Source=${db}" \
      dotnet exec "$DLL" ) >/tmp/smoke-api.log 2>&1 &
  SERVER_PID=$!

  for _ in $(seq 1 45); do
    curl -sf -o /dev/null "$BASE/health" 2>/dev/null && return 0
    sleep 1
  done
  echo "API did not come up. Log:"; tail -30 /tmp/smoke-api.log
  return 1
}

status() { curl -s -o /tmp/smoke-body.json -w '%{http_code}' "$@"; }

printf '\n=== Building once, so each phase starts a single killable process ===\n'
dotnet build "$BACKEND_DIR" -v q --nologo >/tmp/smoke-build.log 2>&1 \
  || { echo "build failed:"; grep -E 'error' /tmp/smoke-build.log | head; exit 1; }
echo "  built."

# =========================================================================================
printf '\n=== Enforcement DISABLED (no CallerIdentity:* configured) ===\n'
start_api DOTNET_ENVIRONMENT=Development || exit 1

grep -q "CallerIdentity is DISABLED" /tmp/smoke-api.log \
  && ok "startup log warns that enforcement is off" \
  || no "expected a DISABLED warning at startup"

S="$(status "$BASE/api/quotes")"
[ "$S" = "200" ] && ok "GET /api/quotes -> 200 (Week-1 behaviour unchanged)" \
                 || no "GET /api/quotes -> $S (expected 200)"

S="$(status "$BASE/health")"
[ "$S" = "200" ] && ok "GET /health -> 200" || no "GET /health -> $S"

# =========================================================================================
printf '\n=== Enforcement ENABLED ===\n'
start_api DOTNET_ENVIRONMENT=Development \
  CallerIdentity__TenantId='8d46a076-d093-416d-a57b-8692cde13bf8' \
  CallerIdentity__Audience='api://smoke-test-audience' \
  CallerIdentity__RequiredRole='Api.Invoke' || exit 1

grep -q "CallerIdentity is ENABLED" /tmp/smoke-api.log \
  && ok "startup log confirms enforcement is on" \
  || no "expected an ENABLED message at startup"

S="$(status "$BASE/api/quotes")"
[ "$S" = "401" ] && ok "GET /api/quotes with no caller token -> 401" \
                 || no "GET /api/quotes -> $S (expected 401)"
printf '         body: %s\n' "$(cat /tmp/smoke-body.json)"

S="$(status "$BASE/api/whoami")"
[ "$S" = "401" ] && ok "GET /api/whoami with no caller token -> 401" \
                 || no "GET /api/whoami -> $S (expected 401)"

S="$(status -H 'X-Caller-Token: Bearer not.a.real.token' "$BASE/api/quotes")"
[ "$S" = "401" ] && ok "garbage caller token -> 401" || no "garbage caller token -> $S"
grep -q 'caller-token-invalid' /tmp/smoke-body.json \
  && ok "rejection names the caller token, not the user's credentials" \
  || no "rejection body did not identify the caller token as the cause"

# Login answers 401 for bad credentials too, so the status code alone proves nothing here.
# The body is what distinguishes "rejected at the front door" from "wrong password".
S="$(status -X POST "$BASE/api/auth/login" -H 'Content-Type: application/json' \
       -d '{"email":"a@b.c","password":"whatever"}')"
if [ "$S" = "401" ] && grep -q 'caller-token-invalid' /tmp/smoke-body.json; then
  ok "POST /api/auth/login is refused at the caller-token layer, before credentials are read"
else
  no "POST /api/auth/login -> $S body=$(cat /tmp/smoke-body.json) (expected 401 caller-token-invalid)"
fi

# The one that matters: the probe path must stay open, or Container Apps kills the revision.
S="$(status "$BASE/health")"
[ "$S" = "200" ] && ok "GET /health -> 200 — probe path is NOT behind enforcement" \
                 || no "GET /health -> $S; this would fail every liveness probe"

printf '\n%d passed, %d failed\n\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ] || exit 1
