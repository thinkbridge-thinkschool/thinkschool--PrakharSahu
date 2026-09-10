#!/usr/bin/env bash
#
# Day 26 — run every query in kql/ and assert the trace actually stitched.
#
#   ./scripts/query-kql.sh
#
# Read-only. Exits non-zero if the distributed trace did not span both roles.
#
# ---------------------------------------------------------------------------------------------
# WHY THIS ASSERTS RATHER THAN JUST PRINTING
#
# The deliverable is "confirm distributed tracing stitches API -> worker -> DB". A script that
# prints four tables and exits zero confirms nothing — somebody still has to read them and know
# what they should contain.
#
# The trace-stitch query is written to return ZERO rows when propagation is broken, so a row
# count is a real test. This turns that into a pass or a fail.
# ---------------------------------------------------------------------------------------------
set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.." || exit 1

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-observability-dev}"
LOOKBACK="${LOOKBACK:-1h}"

die() { printf '\n%s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------------------------
# Query through APPLICATION INSIGHTS, not the Log Analytics workspace directly.
#
# The two endpoints expose the same data under different table names:
#
#   via App Insights   requests, dependencies, traces          <- what the portal Logs blade uses
#   via the workspace  AppRequests, AppDependencies, AppTraces
#
# Querying the workspace failed every file with:
#
#   SEM0100: 'where' operator: Failed to resolve table or column expression named 'requests'
#
# The files in kql/ are written in the App Insights schema deliberately — they are meant to be
# pasted straight into the portal — so the runner uses the endpoint that speaks it.
# ---------------------------------------------------------------------------------------------
APPI_NAME="$(az monitor app-insights component show -g "$RESOURCE_GROUP" \
  --query "[0].name" -o tsv 2>/dev/null | tr -d '\r')"
[ -n "$APPI_NAME" ] || die "No Application Insights component in $RESOURCE_GROUP. Run ./scripts/deploy.sh."

# ---------------------------------------------------------------------------------------------
# ALWAYS -o json, then format locally.
#
# `az monitor app-insights query` renders nothing usable for -o table or -o tsv against this API.
# Table printed an empty block for all four queries, and tsv returned a row COUNT where values
# were expected — so "roles seen: 1" was the number 1, not a role name, and an assertion looking
# for 'quotes-worker' failed against data that was present all along.
#
# JSON is the only shape the CLI returns faithfully, so every call asks for it and the rendering
# happens here where it can be trusted.
# ---------------------------------------------------------------------------------------------
appi_json() {
  az monitor app-insights query --app "$APPI_NAME" -g "$RESOURCE_GROUP" \
    --analytics-query "$1" -o json 2>&1
}

RENDER='
import json, sys
raw = sys.stdin.read()
try:
    t = json.loads(raw)["tables"][0]
except Exception:
    print("  " + raw.strip()[:600]); raise SystemExit
cols = [c["name"] for c in t["columns"]]
rows = [["" if v is None else str(v) for v in r] for r in t["rows"]]
if not rows:
    print("  (no rows)"); raise SystemExit
w = [max([len(cols[i])] + [len(r[i]) for r in rows]) for i in range(len(cols))]
print("  " + "  ".join(cols[i].ljust(w[i]) for i in range(len(cols))))
print("  " + "  ".join("-" * w[i] for i in range(len(cols))))
for r in rows:
    print("  " + "  ".join(r[i].ljust(w[i]) for i in range(len(cols))))
print("\n  %d row(s)" % len(rows))
'

VALUES='
import json, sys
try:
    t = json.loads(sys.stdin.read())["tables"][0]
    for r in t["rows"]:
        print("\t".join("" if v is None else str(v) for v in r))
except Exception:
    pass
'

COUNT='
import json, sys
try:
    print(len(json.loads(sys.stdin.read())["tables"][0]["rows"]))
except Exception:
    print(0)
'

appi()        { appi_json "$1" | python -c "$RENDER"; }
appi_values() { appi_json "$1" | python -c "$VALUES"; }
count_rows()  { appi_json "$1" | python -c "$COUNT"; }

mkdir -p docs
OUT="docs/kql-results.txt"
: > "$OUT"

run_query() {
  local file="$1" title="$2"
  {
    echo "=============================================================================="
    echo " ${title}"
    echo " ${file}"
    echo "=============================================================================="
    # The .kql files carry // comments and blank lines, both of which KQL accepts, so each file
    # is passed through unchanged. What is reviewed is exactly what ran.
    appi "$(cat "$file")"
    echo
  } | tee -a "$OUT"
}

{
  echo "Application Insights: ${APPI_NAME}"
  echo "Lookback: ${LOOKBACK}"
  echo
} | tee -a "$OUT"

run_query kql/01-latency-percentiles.kql  "1. p50 / p95 / p99 by endpoint"
run_query kql/02-dependency-breakdown.kql "2. Dependency call breakdown"
run_query kql/03-error-rate.kql           "3. Error rate over time"
run_query kql/04-distributed-trace.kql    "4. Distributed trace: spans crossing both roles"

# ---------------------------------------------------------------------------------------------
# The assertions.
#
# Each is phrased so that "no data" FAILS rather than passes. An empty workspace must not be able
# to produce a green run — that is the failure mode this codebase keeps rediscovering.
# ---------------------------------------------------------------------------------------------
{
echo "=============================================================================="
echo " Assertions"
echo "=============================================================================="

FAILURES=0
ok()  { printf '  PASS  %s\n' "$1"; }
bad() { printf '  FAIL  %s\n' "$1"; FAILURES=$((FAILURES+1)); }

# 1. Telemetry arrived at all.
REQ="$(count_rows "requests | where timestamp > ago(${LOOKBACK}) | where cloud_RoleName == 'quotes-api' | take 1")"
[ "${REQ:-0}" -ge 1 ] \
  && ok "the api role reported request telemetry" \
  || bad "no request telemetry from 'quotes-api' — ingestion may still be in flight (allow 2-3 min)"

# 2. BOTH roles reported. Without this a trace cannot be distributed, whatever else says.
ROLES="$(appi_values "union (requests),(dependencies) | where timestamp > ago(${LOOKBACK}) | distinct cloud_RoleName" \
  | tr -d '\r' | sort | paste -sd, -)"
echo "  roles seen: ${ROLES:-<none>}"
case "$ROLES" in *quotes-api*)    API_SEEN=1 ;; *) API_SEEN=0 ;; esac
case "$ROLES" in *quotes-worker*) WRK_SEEN=1 ;; *) WRK_SEEN=0 ;; esac
{ [ "$API_SEEN" = 1 ] && [ "$WRK_SEEN" = 1 ]; } \
  && ok "both 'quotes-api' and 'quotes-worker' reported telemetry" \
  || bad "only one role reported; a trace cannot span a boundary that is not there"

# 3. THE deliverable: a trace containing spans from more than one role.
STITCHED="$(count_rows "
union (requests),(dependencies)
| where timestamp > ago(${LOOKBACK})
| summarize roleCount = dcount(cloud_RoleName) by operation_Id
| where roleCount > 1
| take 5")"
echo "  multi-role traces found: ${STITCHED:-0}"
[ "${STITCHED:-0}" -ge 1 ] \
  && ok "distributed tracing stitches the API and the worker into ONE trace" \
  || bad "no trace spans both roles — the outbox traceparent is not being propagated"

# 4. The DB tier, so the chain is API -> worker -> DB and not merely API -> worker.
DB="$(count_rows "
dependencies
| where timestamp > ago(${LOOKBACK})
| where type has 'sql' or type has 'sqlite' or target has 'quotes.db'
| take 1")"
[ "${DB:-0}" -ge 1 ] \
  && ok "database dependency spans are present (the DB tier)" \
  || bad "no database spans — EF Core instrumentation is not reporting"

# 5. The re-parenting was real, not a coincidence. OutboxRelay tags every publish.
REPARENTED="$(appi_values "
dependencies
| where timestamp > ago(${LOOKBACK})
| where name has 'outbox.publish'
| extend reparented = tostring(customDimensions['outbox.reparented'])
| summarize spans = count() by reparented" | tr -d '\r')"
echo "  outbox.publish spans by reparented flag:"
printf '%s\n' "${REPARENTED:-  (none)}" | sed 's/^/    /'
printf '%s' "$REPARENTED" | grep -qi "true" \
  && ok "outbox publishes were re-parented from the stored traceparent" \
  || bad "no publish span reports reparented=true; the linkage may come from somewhere else"

# 6. The alert rule is deployed and enabled.
ALERT="$(az monitor scheduled-query show -g "$RESOURCE_GROUP" -n alert-quotes-error-rate \
  --query "[enabled, severity, windowSize]" -o tsv 2>/dev/null | tr -d '\r' | paste -sd' ' -)"
echo "  alert rule (enabled severity window): ${ALERT:-<not found>}"
printf '%s' "$ALERT" | grep -qi "true" \
  && ok "the error-rate alert rule is deployed and enabled" \
  || bad "the error-rate alert rule is missing or disabled"

echo
echo "=============================================================================="
printf ' %d failed\n' "$FAILURES"
echo "=============================================================================="
} | tee -a "$OUT"

# The block above runs in a pipeline, so its subshell variables do not survive. Re-derive the
# verdict from what was actually written rather than from a counter that cannot have changed.
! grep -q "^  FAIL" "$OUT"
