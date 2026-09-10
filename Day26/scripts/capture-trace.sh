#!/usr/bin/env bash
#
# Day 26 — capture one complete distributed trace and render it.
#
#   ./scripts/capture-trace.sh
#
# Writes docs/trace-spans.json (raw query output) and docs/distributed-trace.png (the waterfall).
#
# ---------------------------------------------------------------------------------------------
# WHICH TRACE IT PICKS, AND WHY THAT MATTERS
#
# Not "the most recent" — the one with the MOST spans among traces that cross both roles.
#
# Recency is the obvious choice and it is wrong here. The last trace produced by a run is often
# a bare GET, or a POST whose outbox row had not been published yet when the exporter flushed.
# Either produces a short, unremarkable waterfall that demonstrates nothing, and the picture is
# the deliverable.
#
# Filtering on `roleCount > 1` first means a trace that does not cross the process boundary can
# never be selected, so the script cannot quietly produce a screenshot of the wrong thing.
# ---------------------------------------------------------------------------------------------
set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.." || exit 1

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-observability-dev}"
LOOKBACK="${LOOKBACK:-30m}"

die() { printf '\n%s\n' "$*" >&2; exit 1; }

APPI_NAME="$(az monitor app-insights component show -g "$RESOURCE_GROUP" \
  --query "[0].name" -o tsv 2>/dev/null | tr -d '\r')"
[ -n "$APPI_NAME" ] || die "No Application Insights component in $RESOURCE_GROUP."

mkdir -p docs

echo "Selecting the richest multi-role trace from the last ${LOOKBACK}..."

az monitor app-insights query --app "$APPI_NAME" -g "$RESOURCE_GROUP" --analytics-query "
let stitched = toscalar(
    union (requests),(dependencies)
    | where timestamp > ago(${LOOKBACK})
    | summarize roleCount = dcount(cloud_RoleName), spans = count(), started = min(timestamp)
        by operation_Id
    | where roleCount > 1
    | top 1 by spans desc
    | project operation_Id);
union
    (requests     | where operation_Id == stitched | extend telemetry = 'request',    dep = ''),
    (dependencies | where operation_Id == stitched | extend telemetry = 'dependency', dep = type)
| project timestamp, role = cloud_RoleName, telemetry, dep, name,
          durationMs = round(duration, 1), id, parent = operation_ParentId, operation_Id
| order by timestamp asc" -o json > docs/trace-spans.json 2>&1

SPANS="$(python -c "
import json, io
try:
    d = json.load(io.open('docs/trace-spans.json', encoding='utf-8'))
    print(len(d['tables'][0]['rows']))
except Exception:
    print(0)
")"

# Assert before rendering. Rendering an empty result would produce a blank image that still looks
# like a deliverable, which is exactly the kind of quiet failure this project keeps finding.
[ "${SPANS:-0}" -ge 2 ] || {
  head -c 400 docs/trace-spans.json
  die "
Only ${SPANS} span(s) returned. Either telemetry has not finished ingesting (allow 2-3 minutes
after run-trace.sh) or no trace crossed both roles."
}

echo "  captured ${SPANS} spans."
node scripts/render-trace.mjs
