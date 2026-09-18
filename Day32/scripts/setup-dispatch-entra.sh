#!/usr/bin/env bash
#
# Register the Entra application that guards Dispatch, and its two roles.
#
#   bash scripts/setup-dispatch-entra.sh
#
# Idempotent: run it twice and the second run changes nothing. Separate from deploy.sh because it
# writes to the DIRECTORY, not to the subscription — a different blast radius, a different set of
# permissions, and something you want to run deliberately rather than as a side effect of shipping.
#
# ---------------------------------------------------------------------------------------------------
# WHY A SECOND APP REGISTRATION
#
# Day 17 registered `quotes-api-day17` and this deployment reuses it for the Quotes API. Dispatch
# gets its own, and that is deliberate: an app registration IS an audience, and an audience is the
# answer to "which API is this token for". Sharing one would mean a token minted for Quotes is
# accepted by Dispatch, so the audience check stops being a boundary and becomes a formality.
#
# ---------------------------------------------------------------------------------------------------
# THE ROLES COME FROM THE DOMAIN
#
# Not from a permissions vocabulary. Dispatch's state machine already separates the person who
# decides work should happen from the person who reports that it did, so those are the roles:
#
#   Dispatch.Dispatcher   raise, triage, schedule, cancel
#   Dispatch.Technician   start, log labour, complete
#   Dispatch.Read         read a work order, read invoices
#
# Labour hours become an invoice. The principal recording the hours should not also be the one that
# committed the customer to the visit, which is why a technician cannot schedule.

set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_tool az   "Install the Azure CLI."
require_tool node "node is used to derive stable role ids."

APP_NAME="${DISPATCH_APP_NAME:-dispatch-api-day32}"

az account show >/dev/null 2>&1 || die "Not signed in. Run: az login"
az account set --subscription "$SUBSCRIPTION_ID"
TENANT_ID="$(az account show --query tenantId -o tsv)"

# Role ids must be GUIDs and must be STABLE: changing one revokes every assignment that used it,
# silently, because the assignment points at the id and not the name. Derived from a fixed
# namespace so a re-run reproduces them exactly rather than minting new ones.
role_id() {
  node -e '
    const { createHash } = require("crypto");
    const h = createHash("sha1").update("dispatch-api-day32:" + process.argv[1]).digest();
    const b = Buffer.from(h.subarray(0, 16));
    b[6] = (b[6] & 0x0f) | 0x50;            // version 5
    b[8] = (b[8] & 0x3f) | 0x80;            // RFC 4122 variant
    const s = b.toString("hex");
    console.log(`${s.slice(0,8)}-${s.slice(8,12)}-${s.slice(12,16)}-${s.slice(16,20)}-${s.slice(20)}`);
  ' "$1"
}

DISPATCHER_ID="$(role_id Dispatcher)"
TECHNICIAN_ID="$(role_id Technician)"
READ_ID="$(role_id Read)"

step "Roles"
printf '    %-22s %s\n' "Dispatch.Dispatcher" "$DISPATCHER_ID"
printf '    %-22s %s\n' "Dispatch.Technician" "$TECHNICIAN_ID"
printf '    %-22s %s\n' "Dispatch.Read"       "$READ_ID"

# allowedMemberTypes carries BOTH User and Application on purpose. Application alone is the usual
# choice for an API and would make the role unassignable to a human — so the demo could not acquire
# a token, and neither could anyone debugging it. User alone would stop a service ever calling.
ROLES_JSON="$(node -e '
  const [dispatcher, technician, read] = process.argv.slice(1);
  console.log(JSON.stringify([
    {
      id: dispatcher, value: "Dispatch.Dispatcher", isEnabled: true,
      allowedMemberTypes: ["User", "Application"],
      displayName: "Dispatcher",
      description: "Raise, triage, schedule and cancel work orders. Commits the customer to a visit."
    },
    {
      id: technician, value: "Dispatch.Technician", isEnabled: true,
      allowedMemberTypes: ["User", "Application"],
      displayName: "Field technician",
      description: "Start work, log labour and complete a work order. Cannot schedule one."
    },
    {
      id: read, value: "Dispatch.Read", isEnabled: true,
      allowedMemberTypes: ["User", "Application"],
      displayName: "Reader",
      description: "Read work orders and invoices."
    }
  ]));
' "$DISPATCHER_ID" "$TECHNICIAN_ID" "$READ_ID")"

step "Application registration"
APP_ID="$(az ad app list --display-name "$APP_NAME" --query "[0].appId" -o tsv 2>/dev/null || true)"

if [ -z "$APP_ID" ]; then
  APP_ID="$(az ad app create --display-name "$APP_NAME" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
  ok "created $APP_NAME ($APP_ID)"
else
  ok "$APP_NAME already exists ($APP_ID)"
fi

OBJECT_ID="$(az ad app show --id "$APP_ID" --query id -o tsv)"
IDENTIFIER_URI="api://$APP_ID"

# The roles and the identifier URI go on in one PATCH. Setting appRoles through `az ad app update`
# replaces the whole collection, which is why they are all listed above rather than appended.
az rest --method PATCH \
  --uri "https://graph.microsoft.com/v1.0/applications/$OBJECT_ID" \
  --headers 'Content-Type=application/json' \
  --body "{\"identifierUris\":[\"$IDENTIFIER_URI\"],\"appRoles\":$ROLES_JSON}" \
  --output none
ok "identifier URI $IDENTIFIER_URI, 3 roles"

# An application registration is a definition; a service principal is its presence in THIS tenant.
# Role assignments are made against the service principal, so without one the roles exist and can
# be granted to nobody.
SP_ID="$(az ad sp list --filter "appId eq '$APP_ID'" --query "[0].id" -o tsv 2>/dev/null || true)"
if [ -z "$SP_ID" ]; then
  SP_ID="$(az ad sp create --id "$APP_ID" --query id -o tsv)"
  ok "service principal created"
else
  ok "service principal exists"
fi

step "Granting the operator both roles"
#
# Both, to one human, which is worth being explicit about: this is a demonstration tenant with one
# person in it, and separating dispatcher from technician across two accounts would prove nothing
# that the policy map does not already state. In a real deployment these go to different groups, and
# the fact that a technician CANNOT schedule is the control — not who happens to hold both today.

OPERATOR_ID="$(az ad signed-in-user show --query id -o tsv)"
OPERATOR_UPN="$(az ad signed-in-user show --query userPrincipalName -o tsv)"

grant_role() {
  local role_name="$1" role_uuid="$2"
  local existing
  existing="$(az rest --method GET \
    --uri "https://graph.microsoft.com/v1.0/users/$OPERATOR_ID/appRoleAssignments" \
    --query "value[?appRoleId=='$role_uuid'] | [0].id" -o tsv 2>/dev/null || true)"
  if [ -n "$existing" ]; then
    ok "$role_name already granted"
    return
  fi
  az rest --method POST \
    --uri "https://graph.microsoft.com/v1.0/users/$OPERATOR_ID/appRoleAssignments" \
    --headers 'Content-Type=application/json' \
    --body "{\"principalId\":\"$OPERATOR_ID\",\"resourceId\":\"$SP_ID\",\"appRoleId\":\"$role_uuid\"}" \
    --output none
  ok "$role_name granted"
}

grant_role "Dispatch.Dispatcher" "$DISPATCHER_ID"
grant_role "Dispatch.Technician" "$TECHNICIAN_ID"
grant_role "Dispatch.Read"       "$READ_ID"

step "Done"
cat <<SUMMARY

    application   $APP_NAME
    appId         $APP_ID
    audience      $IDENTIFIER_URI
    tenant        $TENANT_ID
    assigned to   $OPERATOR_UPN

    deploy.sh reads these back by display name, so nothing needs copying by hand.

    To get a token the way the demo does:
      az account get-access-token --resource $IDENTIFIER_URI --query accessToken -o tsv

SUMMARY
