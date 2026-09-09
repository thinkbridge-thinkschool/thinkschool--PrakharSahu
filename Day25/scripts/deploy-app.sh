#!/usr/bin/env bash
#
# Day 25 — publish and deploy the identity API.
#
#   ./scripts/deploy-app.sh
#
# Separate from deploy.sh on purpose: infrastructure and application change at different rates,
# and rebuilding a Service Bus namespace to ship a code fix is the wrong granularity.
set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.." || exit 1

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-identity-dev}"
PUBLISH_DIR="artifacts/publish"
ZIP_PATH="artifacts/identity-api.zip"

die() { printf '\n%s\n' "$*" >&2; exit 1; }

SITE_NAME="$(az webapp list -g "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null)"
[ -n "$SITE_NAME" ] || die "No web app in $RESOURCE_GROUP. Run ./scripts/deploy.sh first."

echo "=== Publishing ==="
rm -rf "$PUBLISH_DIR" "$ZIP_PATH"
mkdir -p artifacts

dotnet publish src/IdentityApi -c Release -o "$PUBLISH_DIR" --nologo -v q \
  || die "Publish failed."
echo "  published to $PUBLISH_DIR"

# Zipped with Python rather than `zip`, which is not present on every Windows shell, or
# Compress-Archive, which writes backslash separators that Kudu then treats as filenames.
python - "$PUBLISH_DIR" "$ZIP_PATH" <<'PY'
import os, sys, zipfile
src, dest = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(dest, 'w', zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(src):
        for f in files:
            full = os.path.join(root, f)
            z.write(full, os.path.relpath(full, src).replace(os.sep, '/'))
print(f"  zipped {dest} ({os.path.getsize(dest) // 1024} KB)")
PY

echo
echo "=== Deploying to ${SITE_NAME} ==="
az webapp deploy \
  --resource-group "$RESOURCE_GROUP" \
  --name "$SITE_NAME" \
  --src-path "$ZIP_PATH" \
  --type zip \
  --async false \
  -o none || die "Deployment failed."

echo "  deployed."

# ---------------------------------------------------------------------------------------------
# A restart, deliberately.
#
# Zip deploy restarts the app anyway, but doing it explicitly makes the Key Vault reference
# resolution unambiguous: references resolve at STARTUP, so the running process is guaranteed to
# have re-read them after this point rather than possibly holding a value from an earlier boot.
# ---------------------------------------------------------------------------------------------
echo
echo "=== Restarting so Key Vault references re-resolve ==="
az webapp restart -n "$SITE_NAME" -g "$RESOURCE_GROUP" -o none
echo "  restarted."

HOST="$(az webapp show -n "$SITE_NAME" -g "$RESOURCE_GROUP" --query defaultHostName -o tsv)"

# The app is starting; /health is the only path Easy Auth lets through unauthenticated, so it is
# the only thing worth polling. Poll rather than sleep a fixed amount: a cold .NET start on B1 is
# usually a few seconds and occasionally much longer.
echo
echo "=== Waiting for /health ==="
for attempt in $(seq 1 30); do
  CODE="$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 "https://${HOST}/health" 2>/dev/null)"
  if [ "$CODE" = "200" ]; then
    echo "  healthy after ${attempt} attempt(s)."
    break
  fi
  printf '  attempt %-2s -> HTTP %s\n' "$attempt" "$CODE"
  sleep 10
done

echo
echo "Site: https://${HOST}"
echo "Next: ./scripts/prove-no-secrets.sh"
