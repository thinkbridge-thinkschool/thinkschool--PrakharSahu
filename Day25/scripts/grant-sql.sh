#!/usr/bin/env bash
#
# Day 25 — apply the database grant the template cannot express.
#
#   ./scripts/grant-sql.sh
#
# Reads the deployment outputs so nothing has to be typed, then runs tools/SqlGrant.
set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.." || exit 1

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-identity-dev}"

die() { printf '\n%s\n' "$*" >&2; exit 1; }

# The most recent successful deployment in the group. Reading the values back beats asking a
# human to copy four identifiers from a terminal they have since scrolled past.
DEPLOYMENT="$(az deployment group list -g "$RESOURCE_GROUP" \
  --query "sort_by([?properties.provisioningState=='Succeeded' && starts_with(name,'identity-')], &properties.timestamp)[-1].name" \
  -o tsv 2>/dev/null)"

[ -n "$DEPLOYMENT" ] || die "No successful 'identity-*' deployment in $RESOURCE_GROUP. Run ./scripts/deploy.sh."

OUT="$(az deployment group show -g "$RESOURCE_GROUP" -n "$DEPLOYMENT" --query properties.outputs -o json)"
val() { printf '%s' "$OUT" | python -c "import json,sys;print(json.load(sys.stdin).get('$1',{}).get('value',''))"; }

SQL_FQDN="$(val sqlServerFqdn)"
SQL_DB="$(val sqlDatabaseName)"
IDENTITY_NAME="$(val identityName)"
IDENTITY_CLIENT_ID="$(val identityClientId)"

[ -n "$SQL_FQDN" ] && [ -n "$IDENTITY_CLIENT_ID" ] || die "Could not read the deployment outputs."

SQL_SERVER="${SQL_FQDN%%.*}"

echo "=== Database grant ==="
echo "  deployment : $DEPLOYMENT"
echo "  server     : $SQL_FQDN"
echo "  database   : $SQL_DB"
echo "  identity   : $IDENTITY_NAME"
echo

# ---------------------------------------------------------------------------------------------
# A TEMPORARY firewall rule for this machine, removed on the way out.
#
# The template admits Azure services only (0.0.0.0-0.0.0.0), which is right: the App Service is
# the only thing that should reach this server in normal operation. But the grant is a T-SQL
# statement run from a workstation, and without a rule the connection is refused before
# authentication is even attempted:
#
#   Cannot open server '<server>' requested by the login. Client with IP address
#   '<ip>' is not allowed to access the server.
#
# That error names a login problem and is a network problem, which is exactly the kind of
# misdirection worth removing.
#
# The rule is added narrowly (one address, not a range), named so its purpose is obvious in the
# portal, and deleted by a trap so it goes away even if the grant fails or the script is
# interrupted. A permanent "allow my laptop" rule is how a server ends up reachable from an
# address nobody recognises two months later.
# ---------------------------------------------------------------------------------------------
CLIENT_IP="$(curl -s --max-time 15 https://api.ipify.org 2>/dev/null)"

if [ -z "$CLIENT_IP" ]; then
  echo "  could not determine this machine's public IP; attempting the grant anyway."
else
  RULE_NAME="temp-grant-$(date +%Y%m%d%H%M%S)"
  echo "  opening a temporary firewall rule for ${CLIENT_IP} (${RULE_NAME})"

  az sql server firewall-rule create \
    --server "$SQL_SERVER" -g "$RESOURCE_GROUP" \
    --name "$RULE_NAME" \
    --start-ip-address "$CLIENT_IP" --end-ip-address "$CLIENT_IP" \
    -o none 2>/dev/null || echo "  NOTE: could not create the firewall rule."

  cleanup() {
    echo
    echo "  removing the temporary firewall rule ${RULE_NAME}"
    az sql server firewall-rule delete \
      --server "$SQL_SERVER" -g "$RESOURCE_GROUP" --name "$RULE_NAME" \
      -o none 2>/dev/null || echo "  NOTE: could not remove ${RULE_NAME}; delete it by hand."
  }
  trap cleanup EXIT

  # The error message says it may take up to five minutes to take effect. In practice it is a
  # few seconds, so the tool's own connect timeout absorbs it.
  sleep 5
fi

echo
dotnet run --project tools/SqlGrant --verbosity quiet -- \
  "$SQL_FQDN" "$SQL_DB" "$IDENTITY_NAME" "$IDENTITY_CLIENT_ID"
