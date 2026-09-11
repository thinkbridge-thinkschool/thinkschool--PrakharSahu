#!/usr/bin/env bash
#
# Day 27 — assert the hardening actually holds.
#
#   ./scripts/verify-hardening.sh
#
# Starts the API, exercises each control, and exits non-zero if any of them is missing.
#
# ---------------------------------------------------------------------------------------------
# WHY THIS EXISTS SEPARATELY FROM THE ZAP SCAN
#
# ZAP's baseline is passive: it reads the traffic a spider produced. It can see that a security
# header is missing, and it cannot see that an endpoint which SHOULD require a token does not,
# because it has no idea which endpoints are meant to be privileged.
#
# Every assertion here is one ZAP structurally cannot make. They are also the ones that regress
# silently - a forgotten `.RequireAuthorization()` breaks nothing visible, and a rate limiter
# attached to the wrong group looks identical to one attached to the right group until somebody
# attacks it.
# ---------------------------------------------------------------------------------------------
set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.." || exit 1

PORT="${PORT:-5322}"
BACKEND="$(pwd)/backend"
BASE="http://127.0.0.1:${PORT}"
V1="${BASE}/api/v1"

PASS=0; FAIL=0
ok()  { printf '  PASS  %s\n' "$1"; PASS=$((PASS+1)); }
bad() { printf '  FAIL  %s\n' "$1"; FAIL=$((FAIL+1)); }
die() { printf '\n%s\n' "$*" >&2; exit 1; }

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

mkdir -p docs logs

# The seeded password is generated HERE and used below. An earlier attempt set it in one shell and
# read it in another, so login failed with a 401 that looked like an auth bug and was a shell
# scoping bug.
export Jwt__Key="$(python -c "import secrets;print(secrets.token_urlsafe(48))")"
export Seed__AdminEmail="seed-admin@example.invalid"
export Seed__AdminPassword="$(python -c "import secrets;print(secrets.token_urlsafe(24))")"

echo "Building..."
( cd "$BACKEND" && dotnet build --nologo -v q ) >/dev/null 2>&1 || die "Build failed."

# Development, so the dev-only endpoints ARE mapped - the point is to prove they are gated by
# environment, and a Production run could not tell "absent because gated" from "absent because
# never written".
echo "Starting the API on ${PORT} (Development)..."
(
  cd "$BACKEND" || exit 1
  SERVICE_ROLE=api ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://127.0.0.1:${PORT}" \
    dotnet run --no-build --no-launch-profile
) > logs/verify.log 2>&1 &

echo -n "Waiting for /health"
for _ in $(seq 1 40); do
  curl -fsS --max-time 2 "${BASE}/health" >/dev/null 2>&1 && { echo " — up."; READY=1; break; }
  echo -n "."; sleep 2
done
[ "${READY:-0}" = "1" ] || { echo; tail -20 logs/verify.log; die "API never became healthy."; }

code() { curl -s -o /dev/null -w '%{http_code}' --max-time 15 "$@"; }

{
echo
echo "=============================================================================="
echo " Day 27 hardening assertions"
echo "=============================================================================="

# ---- 1. deny by default -----------------------------------------------------------------------
echo
echo "--- Authorization is deny-by-default ---"
C="$(code "${V1}/quotes")"
echo "  GET /api/v1/quotes (no token)      -> ${C}"
[ "$C" = "401" ] && ok "protected endpoint refuses an anonymous caller" \
                 || bad "expected 401, got ${C}"

# The two groups that had NO RequireAuthorization before today. This is the regression test for
# the specific bug the threat model found, not just for the policy in general.
C="$(code -X POST "${V1}/cache/reset")"
echo "  POST /api/v1/cache/reset (no token) -> ${C}"
[ "$C" = "401" ] || [ "$C" = "404" ] && ok "cache control is no longer anonymous" \
                                     || bad "expected 401/404, got ${C}"

C="$(code -X POST "${BASE}/upstream/faults" -H 'Content-Type: application/json' -d '{}')"
echo "  POST /upstream/faults (no token)    -> ${C}"
[ "$C" = "401" ] || [ "$C" = "404" ] && ok "fault injection is no longer anonymous" \
                                     || bad "expected 401/404, got ${C}"

# ---- 2. versioning ----------------------------------------------------------------------------
echo
echo "--- Versioning ---"
V="$(curl -s -D - -o /dev/null --max-time 10 "${BASE}/health" | grep -i '^x-api-version:' | tr -d '\r' | awk '{print $2}')"
echo "  X-Api-Version header               -> ${V:-<absent>}"
[ "$V" = "v1" ] && ok "responses declare the version that served them" \
                || bad "X-Api-Version missing or wrong"

# ---- 3. security headers ----------------------------------------------------------------------
echo
echo "--- Security headers ---"
HEADERS="$(curl -s -D - -o /dev/null --max-time 10 "${BASE}/health" | tr -d '\r')"
for h in "X-Content-Type-Options: nosniff" "X-Frame-Options: DENY" "Content-Security-Policy" "Referrer-Policy" "Permissions-Policy"; do
  if printf '%s' "$HEADERS" | grep -qi "^${h%%:*}:"; then ok "${h%%:*} present"; else bad "${h%%:*} MISSING"; fi
done
if printf '%s' "$HEADERS" | grep -qi '^server:'; then
  bad "Server header still advertises the stack"
else
  ok "no Server banner"
fi

# ---- 4. login, then the limits that need a token ----------------------------------------------
echo
echo "--- Input limits ---"
TOKEN="$(curl -fsS --max-time 20 -X POST "${V1}/auth/login" -H 'Content-Type: application/json' \
  -d "{\"email\":\"${Seed__AdminEmail}\",\"password\":\"${Seed__AdminPassword}\"}" 2>/dev/null \
  | python -c "import json,sys;d=json.load(sys.stdin);print(d.get('accessToken') or d.get('token',''))" 2>/dev/null)"

if [ -z "$TOKEN" ]; then
  bad "could not log in; the remaining assertions cannot run"
else
  ok "login succeeded against the anonymous, rate-limited auth group"

  # Body cap. 200 KB against a 64 KB limit -> 413.
  python -c "print('{\"author\":\"A\",\"text\":\"' + 'x'*200000 + '\"}')" > "$(pwd)/logs/big.json"
  C="$(code -X POST "${V1}/quotes" -H "Authorization: Bearer ${TOKEN}" \
        -H 'Content-Type: application/json' --data-binary "@logs/big.json")"
  echo "  200 KB body against a 64 KB cap    -> ${C}"
  [ "$C" = "413" ] && ok "oversized bodies are refused (413)" \
                   || bad "expected 413, got ${C}"

  # Character allow-list. Asserting on the MESSAGE, not just the status, because the domain's
  # own Quote.Create also returns 400 for bad characters — a status-only check would pass
  # whether or not TextGuard ran at all, which is exactly the kind of test that proves nothing.
  BODY="$(curl -s --max-time 15 -X POST "${V1}/quotes" -H "Authorization: Bearer ${TOKEN}" \
        -H 'Content-Type: application/json' \
        -d '{"author":"A","text":"<script>alert(1)</script>"}')"
  echo "  script tag in quote text           -> $(printf '%s' "$BODY" | head -c 110)"
  printf '%s' "$BODY" | grep -q 'may contain letters' \
    && ok "TextGuard rejected it (its wording, not the domain's)" \
    || bad "rejected, but not by TextGuard — the span guard may not be wired in"

  # Over the 1,000-character text limit but far under the 64 KB body cap, so ONLY the length
  # limit can catch it: neither Kestrel nor the character allow-list applies.
  python -c "print('{\"author\":\"A\",\"text\":\"' + 'a'*2000 + '\"}')" > logs/long.json
  BODY="$(curl -s --max-time 15 -X POST "${V1}/quotes" -H "Authorization: Bearer ${TOKEN}" \
        -H 'Content-Type: application/json' --data-binary "@logs/long.json")"
  echo "  2,000-char text against a 1,000 cap -> $(printf '%s' "$BODY" | head -c 110)"
  printf '%s' "$BODY" | grep -q '1000 characters or fewer' \
    && ok "TextGuard enforced the length limit" \
    || bad "the 1,000-character limit did not apply"
fi

# ---- 5. rate limiting -------------------------------------------------------------------------
echo
echo "--- Rate limiting (auth policy: 5/min) ---"
SEEN429=0
CODES=""
for _ in $(seq 1 9); do
  C="$(code -X POST "${V1}/auth/login" -H 'Content-Type: application/json' \
        -d '{"email":"nobody@example.invalid","password":"wrong"}')"
  CODES="${CODES}${C} "
  [ "$C" = "429" ] && SEEN429=1
done
echo "  nine failed logins                 -> ${CODES}"
[ "$SEEN429" = "1" ] && ok "the limiter returns 429 before the ninth attempt" \
                     || bad "no 429 seen; the auth policy is not attached"

# ---- 6. OpenAPI -------------------------------------------------------------------------------
echo
echo "--- OpenAPI ---"
C="$(code "${BASE}/openapi/v1.json")"
echo "  /openapi/v1.json (Development)     -> ${C}"
[ "$C" = "200" ] && ok "the document is served in Development" \
                 || bad "expected 200, got ${C}"

if curl -fsS --max-time 10 "${BASE}/openapi/v1.json" 2>/dev/null | grep -q '"bearer"'; then
  ok "the document declares the bearer security scheme"
else
  bad "no bearer scheme in the OpenAPI document"
fi

echo
echo "=============================================================================="
printf ' %d passed, %d failed\n' "$PASS" "$FAIL"
echo "=============================================================================="
} | tee docs/hardening-verification.txt

! grep -q "^  FAIL" docs/hardening-verification.txt
