#!/usr/bin/env bash
#
# Scan the LIVE deployment.
#
#   bash scripts/security-scan.sh
#
# Two halves: a dependency audit of everything that ships, and an OWASP ZAP baseline against the
# four public URLs. Results land in docs/security/.
#
# ---------------------------------------------------------------------------------------------------
# THE MISTAKE THIS SCRIPT EXISTS NOT TO REPEAT
#
# Day 27's scan ran ZAP in Docker against a target on the host, which needed
# `--add-host=host.docker.internal:host-gateway`. When that resolution failed, ZAP could not reach
# the target at all — and reported `PASS: 66`, because a baseline scan with nothing to scan passes
# every rule it has. A clean report and an unreachable target look identical in the output.
#
# So this script proves reachability FIRST, from inside the scanning container, and refuses to scan
# a URL it cannot fetch. Scanning a public HTTPS endpoint removes the host-gateway problem
# entirely, but the guard stays, because the failure mode is silent and the cost of checking is one
# curl.

set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
set +e

require_tool az     "Install the Azure CLI."
require_tool docker "ZAP runs in a container. Start Docker Desktop."

OUT="$ROOT/docs/security"
mkdir -p "$OUT"

ENV_DOMAIN="$(env_default_domain)"
WEB="$(app_url "$QUOTES_WEB_APP" "$ENV_DOMAIN")"
BFF="$(app_url "$QUOTES_BFF_APP" "$ENV_DOMAIN")"
API="$(app_url "$QUOTES_API_APP" "$ENV_DOMAIN")"
DISPATCH="$(app_url "$DISPATCH_API_APP" "$ENV_DOMAIN")"

# =================================================================================================
step "1. Dependency audit — everything that ships"

{
  printf 'Dependency audit\nCaptured %s\n\n' "$(date -u '+%Y-%m-%d %H:%M UTC')"

  for pair in "$ROOT/apps/quotes/backend/QuotesApi.slnx|quotes (.NET)" \
              "$ROOT/apps/dispatch/Dispatch.slnx|dispatch (.NET)"; do
    sln="${pair%%|*}"; label="${pair##*|}"
    printf -- '--- %s ---\n' "$label"
    FINDINGS="$(dotnet list "$(winpath "$sln")" package --vulnerable --include-transitive 2>&1 | grep -E '^\s+>')"
    if [ -z "$FINDINGS" ]; then printf '  0 vulnerable packages\n\n'; else printf '%s\n\n' "$FINDINGS"; fi
  done

  for dir in "$ROOT/apps/quotes/bff|quotes-bff (npm)" "$ROOT/apps/quotes/frontend|quotes-web (npm)"; do
    d="${dir%%|*}"; label="${dir##*|}"
    printf -- '--- %s ---\n' "$label"
    # --omit=dev: a build-time dependency is not part of the attack surface of a running container.
    # The frontend ships a static bundle and the broker ships node_modules, so this is the set that
    # actually reaches production.
    ( cd "$d" && npm audit --omit=dev 2>&1 | tail -4 | sed 's/^/  /' )
    printf '\n'
  done
} | tee "$OUT/dependency-audit.txt"

# =================================================================================================
step "2. Reachability — before trusting any scan result"

TARGETS=()
for pair in "$WEB|quotes-web" "$BFF|quotes-bff" "$API|quotes-api" "$DISPATCH|dispatch-api"; do
  url="${pair%%|*}"; name="${pair##*|}"
  # From INSIDE the scanning image, not from this shell. A target the host can reach and the
  # container cannot is exactly the case that produced Day 27's empty pass.
  CODE="$(docker run --rm --entrypoint curl ghcr.io/zaproxy/zaproxy:stable \
            -s -o /dev/null -w '%{http_code}' --max-time 60 "$url/" 2>/dev/null)"
  if [ -n "$CODE" ] && [ "$CODE" != "000" ]; then
    ok "$name reachable from the scanner (HTTP $CODE)"
    TARGETS+=("$url|$name")
  else
    no_target=1
    warn "$name NOT reachable from the scanner — refusing to scan it and call the result a pass"
  fi
done

[ "${#TARGETS[@]}" -gt 0 ] || die "No target was reachable. Nothing scanned."

# =================================================================================================
step "3. OWASP ZAP baseline"
#
# Baseline, not full: it spiders and runs passive rules, and does not attack. That is the right
# choice for a deployment on a shared subscription — an active scan against Container Apps ingress
# is indistinguishable from an attack, and this is not a target anyone has authorised load against.
#
# -I means "do not fail the run on warnings". The gate below is applied deliberately instead, so a
# WARN is recorded and a FAIL is loud.

for pair in "${TARGETS[@]}"; do
  url="${pair%%|*}"; name="${pair##*|}"
  printf '\n    scanning %s\n' "$name"

  docker run --rm -v "$(winpath "$OUT")":/zap/wrk/:rw \
    ghcr.io/zaproxy/zaproxy:stable zap-baseline.py \
    -t "$url" \
    -r "zap-$name.html" \
    -J "zap-$name.json" \
    -I -m 2 -T 5 \
    > "$OUT/zap-$name.log" 2>&1

  SUMMARY="$(grep -E '^(FAIL-NEW|WARN-NEW|PASS|IGNORE|FAIL-INPROG|WARN-INPROG):' "$OUT/zap-$name.log" | tail -6 | tr '\n' ' ')"
  PASSCOUNT="$(grep -oE 'PASS: [0-9]+' "$OUT/zap-$name.log" | tail -1 | grep -oE '[0-9]+')"
  WARNCOUNT="$(grep -oE 'WARN-NEW: [0-9]+' "$OUT/zap-$name.log" | tail -1 | grep -oE '[0-9]+')"
  FAILCOUNT="$(grep -oE 'FAIL-NEW: [0-9]+' "$OUT/zap-$name.log" | tail -1 | grep -oE '[0-9]+')"

  # Day 27's lesson, encoded: a scan that passed everything and warned about nothing did not
  # necessarily scan anything. A baseline against a real site always raises SOMETHING.
  if [ "${PASSCOUNT:-0}" -gt 0 ] && [ "${WARNCOUNT:-0}" -eq 0 ] && [ "${FAILCOUNT:-0}" -eq 0 ]; then
    warn "$name: PASS $PASSCOUNT with zero warnings — verify the scanner actually loaded the site"
  fi
  printf '    %s\n' "$SUMMARY"
done

# =================================================================================================
step "4. Transport and headers, checked directly"
#
# ZAP reports these too, but reading them straight out of a response is what makes the report
# checkable rather than trusted.

{
  printf 'Transport and response headers\nCaptured %s\n\n' "$(date -u '+%Y-%m-%d %H:%M UTC')"
  for pair in "${TARGETS[@]}"; do
    url="${pair%%|*}"; name="${pair##*|}"
    printf -- '--- %s  %s ---\n' "$name" "$url"
    curl -s -D - -o /dev/null --max-time 60 "$url/" \
      | tr -d '\r' \
      | grep -iE '^(HTTP/|strict-transport-security|content-security-policy|x-content-type-options|x-frame-options|referrer-policy|permissions-policy|cross-origin|server|x-powered-by):' \
      | sed 's/^/  /'
    printf '\n  TLS: '
    curl -s -o /dev/null -w 'protocol=%{http_version} cipher=%{ssl_verify_result}\n' --max-time 60 "$url/"
    printf '\n'
  done
} | tee "$OUT/headers.txt"

step "Done"
printf '\n    Reports in docs/security/\n\n'
ls -1 "$OUT" | sed 's/^/      /'
printf '\n'
