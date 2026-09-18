#!/usr/bin/env bash
#
# Shared configuration for every Day 32 script.
#
# Nothing here is a secret. Every value below is either a public identifier, a resource name, or
# something read back from Azure at run time. The two actual secrets — the JWT signing key and the
# Service Bus connection string — are never written to a file, never echoed, and never passed on a
# command line where `ps` could see them. They go from `az` straight into a Bicep @secure() parameter
# and from there into Key Vault.

set -euo pipefail

# Git Bash rewrites anything that looks like a Unix path before handing it to a Windows binary,
# which turns an ARM resource id into a mangled `C:/Program Files/Git/subscriptions/...`. Every
# script here passes resource ids to az.cmd, so this is not optional.
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# --- what this deployment owns --------------------------------------------------------------------

SUBSCRIPTION_ID="${SUBSCRIPTION_ID:-132ef106-f8ec-4352-83e4-9bc238274f25}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-ship-day32}"
LOCATION="${LOCATION:-centralindia}"
IMAGE_TAG="${IMAGE_TAG:-day32}"

QUOTES_API_APP="ca-quotes-api"
QUOTES_BFF_APP="ca-quotes-bff"
QUOTES_WEB_APP="ca-quotes-web"
DISPATCH_API_APP="ca-dispatch-api"

# --- what it reuses rather than creates -----------------------------------------------------------
#
# Three things are borrowed from earlier days, and each is borrowed for a reason that costs money to
# ignore:
#
#   the registry     a second Basic registry is a second fixed monthly charge for no benefit
#   the environment  Azure allows ONE per region per subscription; Day 24 hit the limit mid-deploy
#   the Service Bus  a Standard namespace is about Rs 28/day whether or not a message is sent, and
#                    Day 26 already pays for one with the exact topic this API expects
#
# The teardown script knows about all three and deletes none of them.

ACR_NAME="${ACR_NAME:-quotesday17acr22887}"
ACR_RG="${ACR_RG:-rg-quotes-day17}"

MANAGED_ENV_NAME="${MANAGED_ENV_NAME:-thinkschool-env}"
MANAGED_ENV_RG="${MANAGED_ENV_RG:-thinkschool-rg}"

# Day 26's Application Insights. Borrowed for the same reason as the Service Bus: one system's
# traces belong in one place, and a second component costs money to ingest into while making the
# first one look empty.
APPINSIGHTS_NAME="${APPINSIGHTS_NAME:-appi-quotes-dev-6bi37i}"
APPINSIGHTS_RG="${APPINSIGHTS_RG:-rg-observability-dev}"

SERVICEBUS_NAMESPACE="${SERVICEBUS_NAMESPACE:-sb-quotes-dev-6bi37i}"
SERVICEBUS_RG="${SERVICEBUS_RG:-rg-observability-dev}"

# The Entra application that guards the Quotes API, registered on Day 17. Reused rather than
# re-registered: a second app would mean a second audience, and the API would then have to accept
# two, which is how an audience check quietly becomes a formality.
QUOTES_API_APP_ID="${QUOTES_API_APP_ID:-729a2be3-9609-4fd1-b7c5-e658386f9bfd}"
QUOTES_API_APP_ID_URI="api://${QUOTES_API_APP_ID}"
QUOTES_API_ROLE="Api.Invoke"

# Dispatch's OWN app registration, created by scripts/setup-dispatch-entra.sh. Resolved by display
# name rather than pinned by id, so a tenant that has never run the setup script gets a clear
# "not found" instead of a token rejected for the wrong audience.
DISPATCH_APP_NAME="${DISPATCH_APP_NAME:-dispatch-api-day32}"

# --- helpers --------------------------------------------------------------------------------------

BOLD=$'\033[1m'; DIM=$'\033[2m'; RESET=$'\033[0m'
GREEN=$'\033[32m'; RED=$'\033[31m'; YELLOW=$'\033[33m'

step()  { printf '\n%s==> %s%s\n' "$BOLD" "$*" "$RESET"; }
info()  { printf '    %s\n' "$*"; }
dim()   { printf '    %s%s%s\n' "$DIM" "$*" "$RESET"; }
ok()    { printf '    %s[ok]%s %s\n' "$GREEN" "$RESET" "$*"; }
warn()  { printf '    %s[!]%s  %s\n' "$YELLOW" "$RESET" "$*"; }
die()   { printf '\n    %s[fail]%s %s\n\n' "$RED" "$RESET" "$*" >&2; exit 1; }

require_tool() {
  command -v "$1" >/dev/null 2>&1 || die "$1 is not on PATH. $2"
}

# The environment's DNS suffix, which makes every application URL computable BEFORE anything is
# deployed. That is what lets the frontend bundle — which bakes the broker's address in at build
# time — be built in the same pass as everything else, instead of needing a deploy, a read-back and
# a second build.
env_default_domain() {
  az containerapp env show -n "$MANAGED_ENV_NAME" -g "$MANAGED_ENV_RG" \
    --query properties.defaultDomain -o tsv
}

env_resource_id() {
  az containerapp env show -n "$MANAGED_ENV_NAME" -g "$MANAGED_ENV_RG" --query id -o tsv
}

# Git Bash's path translation is turned OFF above, which is required for ARM resource ids and
# equally fatal for file paths: dotnet, docker and az are Windows binaries and cannot open
# /d/ThinkBridge/... . MSBuild is the loudest about it, reading the leading slash as a switch
# prefix and failing with a bare "MSB1001: Unknown switch" that names nothing.
#
# So every path handed to a Windows tool goes through here, and every ARM id does not.
winpath() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

app_url() { printf 'https://%s.%s' "$1" "$2"; }

# Waits for an HTTP endpoint to answer. Generous by default because the first request of the day
# pays a container cold start AND a serverless database resume, back to back.
wait_for_http() {
  local url="$1" expected="${2:-200}" tries="${3:-40}" i code
  for ((i = 1; i <= tries; i++)); do
    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 "$url" || true)
    if [ "$code" = "$expected" ]; then printf '%s' "$code"; return 0; fi
    sleep 5
  done
  printf '%s' "${code:-000}"
  return 1
}
