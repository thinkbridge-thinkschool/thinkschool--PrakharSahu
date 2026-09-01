#!/usr/bin/env bash
#
# Provisions the same topology on a REAL Azure Service Bus namespace.
#
# Not required to complete Day 19 — the emulator enforces identical semantics and costs
# nothing. This exists so the move to Azure is one command rather than a research task, and so
# the exercise's topology is written down as infrastructure rather than described in prose.
#
#   ./scripts/provision-servicebus.sh
#
# Prints the connection string at the end. It is NEVER written to a file in this repository.

set -euo pipefail
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

SUBSCRIPTION_ID="${SUBSCRIPTION_ID:-132ef106-f8ec-4352-83e4-9bc238274f25}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-quotes-day19}"
LOCATION="${LOCATION:-centralindia}"
NAMESPACE="${NAMESPACE:-sb-quotes-day19-$RANDOM}"
TOPIC="${TOPIC:-quote-events}"
MAX_DELIVERY="${MAX_DELIVERY:-3}"

log()  { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
die()  { printf '\n\033[1;31mFAILED: %s\033[0m\n' "$*" >&2; exit 1; }

command -v az >/dev/null || die "az CLI not found."
az account show >/dev/null 2>&1 || die "Not signed in. Run: az login"
az account set --subscription "$SUBSCRIPTION_ID"

log "Namespace '$NAMESPACE'"

# --------------------------------------------------------------------------------------
# Standard, not Basic. This is not a cost preference — the Basic tier does not support
# topics at all, only queues, so a Basic namespace cannot express this exercise. Standard
# carries a monthly base charge; Premium is dedicated capacity and is not needed here.
# --------------------------------------------------------------------------------------
az group create -n "$RESOURCE_GROUP" -l "$LOCATION" --only-show-errors >/dev/null

if ! az servicebus namespace show -n "$NAMESPACE" -g "$RESOURCE_GROUP" >/dev/null 2>&1; then
  info "creating (a few minutes)…"
  az servicebus namespace create \
    -n "$NAMESPACE" -g "$RESOURCE_GROUP" -l "$LOCATION" \
    --sku Standard --only-show-errors >/dev/null
fi
info "ready"

log "Topic '$TOPIC'"
az servicebus topic create \
  --namespace-name "$NAMESPACE" -g "$RESOURCE_GROUP" -n "$TOPIC" \
  --default-message-time-to-live PT1H \
  --only-show-errors >/dev/null
info "created"

# --------------------------------------------------------------------------------------
# Two subscriptions, neither filtered, so each receives every message. That is the whole
# point of a topic over a queue: adding a third reader later means adding a subscription,
# with no change to the publisher and no redeploy.
#
# --max-delivery-count is the setting that produces the dead-letter behaviour. The broker
# counts deliveries and moves the message itself once the count is exceeded — no client
# setting can override it, which is exactly why it is trustworthy.
# --------------------------------------------------------------------------------------
for SUBSCRIPTION in audit search-index; do
  log "Subscription '$SUBSCRIPTION'"
  az servicebus topic subscription create \
    --namespace-name "$NAMESPACE" -g "$RESOURCE_GROUP" \
    --topic-name "$TOPIC" -n "$SUBSCRIPTION" \
    --max-delivery-count "$MAX_DELIVERY" \
    --lock-duration PT1M \
    --dead-lettering-on-message-expiration true \
    --only-show-errors >/dev/null
  info "created with MaxDeliveryCount=$MAX_DELIVERY"
done

log "Connection string"

# Read at run time and printed once. Storing it in this repository would undo Day 17's
# entire point; in Azure it belongs in a Container Apps secret, and locally in an
# environment variable for the session only.
CONNECTION="$(az servicebus namespace authorization-rule keys list \
  --namespace-name "$NAMESPACE" -g "$RESOURCE_GROUP" \
  -n RootManageSharedAccessKey --query primaryConnectionString -o tsv)"

cat <<EOF

Provisioned.

  namespace     : $NAMESPACE
  topic         : $TOPIC
  subscriptions : audit, search-index (MaxDeliveryCount $MAX_DELIVERY)

Run the API against it with:

  export ServiceBus__ConnectionString='$CONNECTION'
  ./scripts/verify-messaging.sh

Then tear it down when you are finished — a Standard namespace bills whether or not it is
carrying traffic:

  az group delete -n $RESOURCE_GROUP --yes --no-wait

A production deployment would drop the connection string entirely and use managed identity
with the Azure Service Bus Data Sender / Data Receiver roles, exactly as Day 17 did. The
emulator only speaks connection strings, which is the single reason this script emits one.
EOF
