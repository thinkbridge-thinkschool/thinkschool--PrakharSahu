#!/usr/bin/env bash
#
# Day 24 — prove the two things a deployment stack gives you that a plain deployment does not.
#
#   ./scripts/drift-proof.sh dev
#
# Read-mostly. It makes exactly one change to a live resource — a tag — and reverts it by
# re-provisioning, which is the point being demonstrated.
#
# ---------------------------------------------------------------------------------------------
# WHAT A PLAIN DEPLOYMENT CANNOT DO
#
# `az deployment group create` applies a template and records the result in a deployment history.
# It does not remember WHICH resources it made. So nothing can answer:
#
#   - "what does this template own right now?"        -> no inventory exists
#   - "somebody deleted a resource, is that allowed?" -> nothing was watching
#   - "delete everything this created"                -> nothing knows what that is
#
# A stack answers all three, because the inventory lives server-side in Azure rather than in
# whatever the last person to run the pipeline happened to have on disk.
# ---------------------------------------------------------------------------------------------
set -uo pipefail
export MSYS_NO_PATHCONV=1

ENVIRONMENT="${1:-dev}"
STACK="azd-stack-${ENVIRONMENT}"
RESOURCE_GROUP="rg-dispatch-${ENVIRONMENT}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.." || exit 1

PASS=0; FAIL=0
ok()  { printf '  PASS  %s\n' "$1"; PASS=$((PASS+1)); }
bad() { printf '  FAIL  %s\n' "$1"; FAIL=$((FAIL+1)); }

# An ARM error names the signed-in principal. That is a real UPN and object id, and this output
# is committed — so it is masked here rather than after the fact.
redact() {
  sed -E -e 's/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+/<user>/g' \
         -e 's/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/<guid>/g'
}

echo "=============================================================================="
echo " Deployment stack drift proof — ${ENVIRONMENT}"
echo "=============================================================================="

# ---------------------------------------------------------------------------------------------
# 1. THE INVENTORY. This is the thing a plain deployment does not have.
# ---------------------------------------------------------------------------------------------
echo
echo "--- 1. The stack knows exactly what it owns -----------------------------------"
SUMMARY="$(az stack sub show --name "$STACK" \
  --query "{state:provisioningState,onUnmanageResources:actionOnUnmanage.resources,onUnmanageResourceGroups:actionOnUnmanage.resourceGroups,denyMode:denySettings.mode,managed:length(resources)}" \
  -o yaml 2>&1)" || { echo "$SUMMARY"; echo "No stack '$STACK'. Provision first."; exit 1; }
echo "$SUMMARY" | sed 's/^/  /'

MANAGED="$(az stack sub show --name "$STACK" --query "resources[].id" -o tsv 2>/dev/null)"
COUNT="$(printf '%s\n' "$MANAGED" | grep -c .)"
echo
echo "  managed resources:"
printf '%s\n' "$MANAGED" | sed 's#.*/providers/##' | sort | sed 's/^/    /'
if [ "$COUNT" -gt 0 ]; then
  ok "the inventory is server-side and enumerable ($COUNT resources)"
else
  bad "the stack reports no managed resources"
fi

# ---------------------------------------------------------------------------------------------
# 2. DRIFT PREVENTED — an out-of-band DELETE is refused by ARM itself.
#
# Not a policy that reports a violation afterwards. The request fails, and it fails for an
# account that HAS delete permission — the error says so explicitly. That is the difference
# between detecting drift and making it impossible.
# ---------------------------------------------------------------------------------------------
echo
echo "--- 2. Deleting a managed resource out of band is REFUSED ----------------------"
TOPIC_NS="$(printf '%s\n' "$MANAGED" | grep -m1 '/namespaces/[^/]*$' | sed 's#.*/##')"

if [ -z "$TOPIC_NS" ]; then
  bad "could not find the Service Bus namespace in the inventory"
else
  echo "  attempting: az servicebus topic delete --name work-order-events"
  DELETE_OUT="$(az servicebus topic delete --name work-order-events \
    --namespace-name "$TOPIC_NS" -g "$RESOURCE_GROUP" 2>&1)"

  printf '%s\n' "$DELETE_OUT" | redact | fold -s -w 84 | head -6 | sed 's/^/    /'

  if printf '%s' "$DELETE_OUT" | grep -q 'DenyAssignmentAuthorizationFailed'; then
    ok "ARM refused the delete, citing the deny assignment created by the stack"
  else
    bad "the delete was NOT refused — deny settings are not in force"
  fi

  # An error message is a claim; the resource still existing is the evidence.
  if az servicebus topic show --name work-order-events \
       --namespace-name "$TOPIC_NS" -g "$RESOURCE_GROUP" --query name -o tsv >/dev/null 2>&1; then
    ok "the topic still exists"
  else
    bad "the topic is gone — the deny assignment did not hold"
  fi
fi

# ---------------------------------------------------------------------------------------------
# 3. DRIFT DETECTED AND CORRECTED — a WRITE is allowed, and the next provision reverts it.
#
# `denyDelete` permits writes on purpose: blocking them would also block the platform's own
# legitimate mutations (a Container Apps revision, a scale event, a certificate rotation) and the
# application would degrade in ways that look like bugs.
#
# So configuration drift IS possible, and this is what closes the gap: the template is the
# declared state, and re-running it puts reality back. The stack does not prevent this drift; it
# makes correcting it a no-argument command rather than an investigation.
# ---------------------------------------------------------------------------------------------
echo
echo "--- 3. Configuration drift is reverted by the next provision -------------------"
SB_ID="$(printf '%s\n' "$MANAGED" | grep -m1 '/namespaces/[^/]*$')"

BEFORE="$(az tag list --resource-id "$SB_ID" --query "properties.tags.managedBy" -o tsv 2>/dev/null)"
echo "  tag managedBy before drift    : ${BEFORE:-<unset>}"

az tag update --resource-id "$SB_ID" --operation Merge \
  --tags managedBy=someone-in-the-portal -o none 2>/dev/null

DRIFTED="$(az tag list --resource-id "$SB_ID" --query "properties.tags.managedBy" -o tsv 2>/dev/null)"
echo "  tag managedBy after  drift    : ${DRIFTED}"

if [ "$DRIFTED" = "someone-in-the-portal" ]; then
  ok "an out-of-band WRITE succeeded, as denyDelete intends"
else
  bad "could not introduce drift; the rest of this section proves nothing"
fi

echo "  re-running: azd provision --no-prompt"
azd provision --no-prompt >/dev/null 2>&1

AFTER="$(az tag list --resource-id "$SB_ID" --query "properties.tags.managedBy" -o tsv 2>/dev/null)"
echo "  tag managedBy after  provision: ${AFTER}"

if [ "$AFTER" = "$BEFORE" ]; then
  ok "the stack restored the declared value; drift is gone"
else
  bad "drift survived the provision (expected ${BEFORE}, got ${AFTER})"
fi

# ---------------------------------------------------------------------------------------------
# 4. What `azd down` would remove. Read-only — it deletes nothing.
# ---------------------------------------------------------------------------------------------
echo
echo "--- 4. Teardown scope: what actionOnUnmanage=delete would remove ---------------"
echo "  the resource group itself, and all ${COUNT} resources listed in section 1."
echo "  Nothing else in the subscription is in this inventory, so nothing else is at risk."
echo
echo "  Contrast with Day 23, which used plain deployments: rg-dispatch-dev and"
echo "  rg-dispatch-prod were created by 'az group create' inside a script, owned by no"
echo "  template, and outlived every deployment that used them."

echo
echo "=============================================================================="
printf ' %d passed, %d failed\n' "$PASS" "$FAIL"
echo "=============================================================================="
[ "$FAIL" -eq 0 ]
