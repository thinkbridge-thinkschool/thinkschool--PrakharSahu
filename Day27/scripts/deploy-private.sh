#!/usr/bin/env bash
#
# Day 27 — deploy the data tier behind private endpoints, then prove it is unreachable publicly.
#
#   ./scripts/deploy-private.sh            deploy, then verify
#   ./scripts/deploy-private.sh --verify   re-run the checks against what is already deployed
#   ./scripts/deploy-private.sh --down     tear it down
#
# ---------------------------------------------------------------------------------------------
# COST WARNING
#
# Roughly USD 1/hour, and almost all of it is one line: Service Bus PREMIUM. Private endpoints are
# unavailable on Standard and Basic, so "put the data tier behind private endpoints" forces the
# dedicated tier whether the workload needs its throughput or not.
#
# That is the sharpest system-design tradeoff in today's exercise, and it is a pricing decision
# rather than a technical one: the same namespace, running the same code, costs roughly seventy
# times more because it is on a VNet.
# ---------------------------------------------------------------------------------------------
set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.." || exit 1

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-quotes-private-dev}"
LOCATION="${LOCATION:-centralindia}"
STAMP="$(date +%Y%m%d-%H%M%S)"

die() { printf '\n%s\n' "$*" >&2; exit 1; }

# The Azure CLI emits CRLF under Git Bash, so every captured value needs the CR stripped or it is
# carried into the next command. Two of three DNS lookups failed with "Operation returned an
# invalid status 'Bad Request'" because of exactly this - an error that reads like an Azure
# problem and was a shell quoting bug. '\015' is the carriage return in octal, which sidesteps
# the backslash escaping that broke three earlier attempts at writing this one line.
strip() { tr -d '\015'; }

if [ "${1:-}" = "--down" ]; then
  echo "Deleting ${RESOURCE_GROUP}..."
  az group delete -n "$RESOURCE_GROUP" --yes --no-wait \
    && echo "  delete started (running in the background)." \
    || die "Could not delete the group."
  exit 0
fi

VERIFY_ONLY=0
[ "${1:-}" = "--verify" ] && VERIFY_ONLY=1

az account show >/dev/null 2>&1 || die "Not signed in. Run 'az login'."
echo "Subscription: $(az account show --query name -o tsv | strip)"

if [ "$VERIFY_ONLY" = "1" ]; then
  # Re-run the checks without paying for a redeployment. Separated because the first verification
  # had three bugs of its own, and none of them were in Azure.
  SQL_NAME="$(az sql server list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv | strip)"
  SB_NAME="$(az servicebus namespace list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv | strip)"
  KV_NAME="$(az keyvault list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv | strip)"
  [ -n "$SQL_NAME" ] || die "Nothing deployed in ${RESOURCE_GROUP}."
  SQL_FQDN="$(az sql server show -n "$SQL_NAME" -g "$RESOURCE_GROUP" \
    --query fullyQualifiedDomainName -o tsv | strip)"
else
  export SQL_ADMIN_OBJECT_ID="${SQL_ADMIN_OBJECT_ID:-$(az ad signed-in-user show --query id -o tsv | strip)}"
  export SQL_ADMIN_LOGIN="${SQL_ADMIN_LOGIN:-$(az ad signed-in-user show --query userPrincipalName -o tsv | strip)}"
  [ -n "$SQL_ADMIN_OBJECT_ID" ] || die "Could not resolve the signed-in user."

  echo "=== Compiling ==="
  ( cd infra && az bicep build --file main.bicep --stdout >/dev/null ) || die "Bicep failed to compile."
  echo "  clean."

  az group create -n "$RESOURCE_GROUP" -l "$LOCATION" -o none || die "Could not create $RESOURCE_GROUP."

  echo
  echo "=== Deploying (Service Bus Premium takes ~15 minutes) ==="
  ( cd infra && az deployment group create \
      --resource-group "$RESOURCE_GROUP" \
      --template-file main.bicep \
      --parameters sqlAdminObjectId="$SQL_ADMIN_OBJECT_ID" sqlAdminLogin="$SQL_ADMIN_LOGIN" \
      --name "private-${STAMP}" \
      -o none ) || die "Deployment failed."
  echo "  deployed."

  OUT="$(az deployment group show -g "$RESOURCE_GROUP" -n "private-${STAMP}" --query properties.outputs -o json)"
  val() { printf '%s' "$OUT" | python -c "import json,sys;print(json.load(sys.stdin).get('$1',{}).get('value',''))"; }

  SQL_FQDN="$(val sqlServerFqdn)"
  SQL_NAME="$(val sqlServerName)"
  SB_NAME="$(val serviceBusName)"
  KV_NAME="$(val keyVaultName)"
fi

mkdir -p docs
{
echo "=============================================================================="
echo " Private endpoint verification"
echo "=============================================================================="

echo
echo "--- 1. Public network access is DISABLED on all three ---"
printf '  SQL         publicNetworkAccess = %s\n' "$(az sql server show -n "$SQL_NAME" -g "$RESOURCE_GROUP" --query publicNetworkAccess -o tsv | strip)"
printf '  ServiceBus  publicNetworkAccess = %s\n' "$(az servicebus namespace show -n "$SB_NAME" -g "$RESOURCE_GROUP" --query publicNetworkAccess -o tsv | strip)"
printf '  KeyVault    publicNetworkAccess = %s\n' "$(az keyvault show -n "$KV_NAME" -g "$RESOURCE_GROUP" --query "properties.publicNetworkAccess" -o tsv | strip)"

echo
echo "--- 2. Each endpoint has a private IP in snet-data ---"
# Read the address off the endpoint's NIC. The obvious query - customDnsConfigs[0].ipAddresses[0]
# - returns null here, and the first version of this script printed "None" three times because of
# it. The NIC always has the address, because the NIC IS the endpoint.
for PE in pe-sql pe-servicebus pe-keyvault; do
  NIC="$(az network private-endpoint show -n "$PE" -g "$RESOURCE_GROUP" --query "networkInterfaces[0].id" -o tsv 2>/dev/null | strip)"
  IP="$(az network nic show --ids "$NIC" --query "ipConfigurations[0].privateIPAddress" -o tsv 2>/dev/null | strip)"
  printf '  %-14s %s\n' "$PE" "${IP:-<none>}"
done

echo
echo "--- 3. Private DNS zones, and the A records Azure wrote into them ---"
for ZONE in $(az network private-dns zone list -g "$RESOURCE_GROUP" --query "[].name" -o tsv | strip); do
  echo "  ${ZONE}"
  az network private-dns record-set a list -g "$RESOURCE_GROUP" -z "$ZONE" \
    --query "[].{record:name, ip:aRecords[0].ipv4Address}" -o tsv 2>&1 | sed 's/^/    /'
done

echo
echo "--- 4. THE PROOF: a data-plane call from outside the VNet is refused ---"
echo
echo "  az keyvault secret list --vault-name ${KV_NAME}"
az keyvault secret list --vault-name "$KV_NAME" --query "[].name" -o tsv 2>&1 | head -4 | sed 's/^/    /'

echo
echo "--- 5. Why a TCP connect is NOT a valid test for Azure SQL ---"
#
# The first version of this script opened a socket to <server>.database.windows.net:1433, found it
# CONNECTED, and concluded public access was not blocked. That conclusion was wrong.
#
# Azure SQL's public endpoint is a shared REGIONAL GATEWAY fronting every SQL server in the
# region, so the TCP handshake terminates there and succeeds regardless of any one server's
# setting. `publicNetworkAccess: Disabled` is enforced when the gateway routes the LOGIN to the
# server, a layer above TCP.
#
# So an open port proves nothing either way, and a scanner reporting "1433 reachable" against
# Azure SQL is reporting the gateway. Key Vault above is the clean demonstration, because it
# speaks HTTPS and refuses at the application layer, in words.
PUBLIC_IP="$(python -c "
import socket
try: print(socket.gethostbyname('${SQL_FQDN}'))
except Exception as exc: print('resolution failed:', exc)
" 2>/dev/null)"
echo "  ${SQL_FQDN}"
echo "    resolves to : ${PUBLIC_IP}   <- the regional gateway, not the server"
printf '    TCP 1433    : '
python - <<PY
import socket
s = socket.socket(); s.settimeout(12)
try:
    s.connect(('${SQL_FQDN}', 1433))
    print('open  <- the GATEWAY answered. Says nothing about this server; see above.')
except Exception as exc:
    print(f'refused ({type(exc).__name__})')
finally:
    s.close()
PY

echo
echo "  Inside the VNet the same name resolves to the private IP in section 3, because the"
echo "  private DNS zone is linked to that VNet and overrides the public answer. No connection"
echo "  string changes anywhere, which is the whole point of doing it with DNS."
} | tee docs/private-endpoint-verification.txt

cat <<EOF

=============================================================================================
 ${RESOURCE_GROUP}
=============================================================================================

  Roughly USD 1/hour, dominated by Service Bus Premium.

  Tear down:  ./scripts/deploy-private.sh --down

EOF
