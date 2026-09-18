#!/usr/bin/env bash
#
# Record what Azure actually contains, so the write-up quotes reality rather than intent.
#
#   bash scripts/capture-deployment.sh > docs/deployment.txt
#
# Everything here is read-only. It prints identifiers — resource names, client ids, hostnames — and
# no secrets: the two Key Vault secrets are listed by NAME and their values are never fetched. The
# SQL connection strings are printed in full precisely because there is nothing in them to redact,
# which is the claim this file exists to substantiate.

set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

rule() { printf '\n%s\n %s\n%s\n' "$(printf '=%.0s' {1..88})" "$*" "$(printf '=%.0s' {1..88})"; }

az account set --subscription "$SUBSCRIPTION_ID" 2>/dev/null

rule "SUBSCRIPTION"
az account show --query "{name:name, id:id, tenant:tenantId}" -o tsv | sed 's/^/  /'

rule "RESOURCE GROUP  $RESOURCE_GROUP"
az resource list -g "$RESOURCE_GROUP" --query "sort_by([].{name:name, type:type}, &type)" -o table 2>/dev/null

rule "APPLICATIONS"
az containerapp list -g "$RESOURCE_GROUP" \
  --query "sort_by([].{name:name, url:properties.configuration.ingress.fqdn, replicas:properties.template.scale.minReplicas, image:properties.template.containers[0].image}, &name)" \
  -o table 2>/dev/null

rule "THE CLAIM: NO PASSWORD ANYWHERE"
echo
echo "  Every environment variable the two APIs carry, in full. A connection string appears here"
echo "  in plain text because there is no credential in it to protect: the driver is told to get a"
echo "  token from the platform, and 'User Id' names WHICH identity by client id — a public value."
for app in "$QUOTES_API_APP" "$DISPATCH_API_APP"; do
  printf '\n  --- %s ---\n' "$app"
  az containerapp show -n "$app" -g "$RESOURCE_GROUP" \
    --query "properties.template.containers[0].env[].{name:name, value:value, secretRef:secretRef}" \
    -o table 2>/dev/null | sed 's/^/  /'
done

echo
echo "  The rows with a secretRef and no value are Key Vault references. The platform resolves"
echo "  them at container start using the managed identity; the values are not in the template,"
echo "  not in deployment history, and not returned by 'az containerapp show'."
printf '\n  --- secrets declared by %s ---\n' "$QUOTES_API_APP"
az containerapp secret list -n "$QUOTES_API_APP" -g "$RESOURCE_GROUP" \
  --query "[].{name:name, keyVaultUrl:keyVaultUrl}" -o table 2>/dev/null | sed 's/^/  /'

rule "SQL — ENTRA ONLY, NO SQL LOGINS"
SQL_SERVER="$(az sql server list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null)"
if [ -n "$SQL_SERVER" ]; then
  az sql server show -n "$SQL_SERVER" -g "$RESOURCE_GROUP" \
    --query "{server:name, fqdn:fullyQualifiedDomainName, entraOnly:administrators.azureADOnlyAuthentication, adminType:administrators.principalType}" \
    -o tsv | sed 's/^/  /'
  printf '\n  databases (serverless, auto-pause 60 min):\n'
  az sql db list -s "$SQL_SERVER" -g "$RESOURCE_GROUP" \
    --query "[?name!='master'].{name:name, sku:currentServiceObjectiveName, status:status, autoPauseMin:autoPauseDelay}" \
    -o table 2>/dev/null | sed 's/^/  /'
  printf '\n  firewall rules:\n'
  az sql server firewall-rule list -s "$SQL_SERVER" -g "$RESOURCE_GROUP" \
    --query "[].{name:name, start:startIpAddress, end:endIpAddress}" -o table 2>/dev/null | sed 's/^/  /'
  echo
  echo "  0.0.0.0-0.0.0.0 is the documented sentinel for 'any Azure service'. It is not the whole"
  echo "  internet: the server still refuses anything that cannot present an Entra token for a"
  echo "  principal it has a database user for."
fi

rule "IDENTITY — THE THREE IDS"
IDENTITY="$(az identity list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null)"
if [ -n "$IDENTITY" ]; then
  az identity show -n "$IDENTITY" -g "$RESOURCE_GROUP" \
    --query "{name:name, clientId:clientId, principalId:principalId}" -o tsv | sed 's/^/  /'
  echo
  echo "  clientId     what SQL derives the user SID from, and what the connection strings carry"
  echo "  principalId  what role assignments and the Entra app-role grant name"
  echo "  Using one where the other belongs deploys cleanly and then refuses every login."
fi

rule "WHAT IS BORROWED FROM EARLIER DAYS"
cat <<'BORROWED'

  registry        quotesday17acr22887        Day 17   a second Basic registry is a second
                                                      fixed monthly charge for no benefit
  environment     thinkschool-env            Day 17   Azure permits ONE per region per
                                                      subscription; the allowance is spent
  service bus     sb-quotes-dev-6bi37i       Day 26   a Standard namespace is ~Rs 28/day
                                                      standing, and this one already has the
                                                      quote-events topic the API expects
  app insights    appi-quotes-dev-6bi37i     Day 26   one system's traces belong in one place
  entra app       quotes-api-day17           Day 17   a second audience is how an audience
                                                      check becomes a formality

  teardown.sh deletes the resource group and none of these.
BORROWED

rule "END"
