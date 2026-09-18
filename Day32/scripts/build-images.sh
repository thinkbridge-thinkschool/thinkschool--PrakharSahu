#!/usr/bin/env bash
#
# Build and push all four images.
#
#   bash scripts/build-images.sh
#
# ---------------------------------------------------------------------------------------------------
# WHY THERE ARE TWO BUILD MECHANISMS IN ONE SCRIPT
#
# ---------------------------------------------------------------------------------------------------
#
# `az acr build` is the obvious answer — build server-side, no local daemon, no credentials to
# juggle. It does not work here:
#
#   ERROR: (TasksOperationsNotAllowed) ACR Tasks requests for the registry ... are not permitted.
#
# ACR Tasks are blocked on this student subscription. So the builds happen locally, and the two
# .NET images use the SDK's own container support rather than Docker:
#
#   dotnet publish /t:PublishContainer   builds an OCI image and pushes it over HTTPS, no daemon
#   docker build + docker push           needed for the Node broker and the nginx frontend
#
# The .NET path is preferred wherever it applies because it removes a moving part, and because a
# Dockerfile that only ever builds on a machine with Docker running is a Dockerfile that quietly
# stops being tested. Dispatch has no Dockerfile at all for that reason. The Quotes API keeps its
# Dockerfile because Day 27 wrote one and it has been kept honest by being built here.
#
# ---------------------------------------------------------------------------------------------------
# THE ORDERING CONSTRAINT THAT IS EASY TO MISS
#
# The Angular bundle bakes the broker's URL in at BUILD time, via environment.production.ts. So the
# frontend cannot be built until that URL is known — which normally means deploy, read the FQDN
# back, then build and redeploy.
#
# It is avoidable: a Container App's FQDN is `<app-name>.<environment-default-domain>`, and the
# environment already exists. The URL is therefore computable before anything is deployed, and all
# four images are built in one pass. The same computed value is stamped into the bundle here and
# passed to the CSP at run time, so the address the app calls and the address it is allowed to call
# cannot drift apart.

set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_tool az     "Install the Azure CLI."
require_tool docker "Start Docker Desktop — the Node and nginx images need it."
require_tool dotnet "Install the .NET 10 SDK."
require_tool node   "Node is needed to stamp the frontend environment file."

docker info >/dev/null 2>&1 || die "The Docker daemon is not responding. Start Docker Desktop and wait for the whale icon to settle."

step "Resolving the environment"
ENV_DOMAIN="$(env_default_domain)"
[ -n "$ENV_DOMAIN" ] || die "Could not read the default domain of $MANAGED_ENV_NAME."
BFF_URL="$(app_url "$QUOTES_BFF_APP" "$ENV_DOMAIN")"
info "environment  $MANAGED_ENV_NAME"
info "domain       $ENV_DOMAIN"
info "broker URL   $BFF_URL   (computed, not read back from a deployment)"

step "Authenticating to the registry"
# --expose-token returns an ACR *refresh* token. It cannot be used against the registry API
# directly; both `docker login` and the .NET SDK exchange it for an access token themselves, using
# the documented null-GUID username. Captured into a variable and never echoed.
ACR_TOKEN="$(az acr login -n "$ACR_NAME" --expose-token --query accessToken -o tsv)"
[ -n "$ACR_TOKEN" ] || die "Could not obtain a registry token. Is 'az login' still valid?"
ACR_USER="00000000-0000-0000-0000-000000000000"
printf '%s' "$ACR_TOKEN" | docker login "$ACR_NAME.azurecr.io" -u "$ACR_USER" --password-stdin >/dev/null
ok "registry $ACR_NAME.azurecr.io"

export SDK_CONTAINER_REGISTRY_UNAME="$ACR_USER"
export SDK_CONTAINER_REGISTRY_PWORD="$ACR_TOKEN"

publish_dotnet_image() {
  local project="$1" repository="$2"
  step "Building $repository  (SDK container publish, no daemon)"
  dotnet publish "$(winpath "$project")" -c Release /t:PublishContainer \
    -p ContainerRegistry="$ACR_NAME.azurecr.io" \
    -p ContainerRepository="$repository" \
    -p ContainerImageTags="\"$IMAGE_TAG\"" \
    --os linux --arch x64 --nologo 2>&1 | grep -E "Pushed image|error" || true
  az acr repository show-tags -n "$ACR_NAME" --repository "$repository" -o tsv 2>/dev/null \
    | grep -qx "$IMAGE_TAG" || die "$repository:$IMAGE_TAG is not in the registry after publish."
  ok "$repository:$IMAGE_TAG"
}

build_docker_image() {
  local context="$1" repository="$2"
  step "Building $repository  (docker)"
  docker build -q -t "$ACR_NAME.azurecr.io/$repository:$IMAGE_TAG" "$(winpath "$context")" >/dev/null \
    || die "docker build failed for $repository."
  docker push -q "$ACR_NAME.azurecr.io/$repository:$IMAGE_TAG" >/dev/null \
    || die "docker push failed for $repository."
  ok "$repository:$IMAGE_TAG"
}

# --- 1. Dispatch ----------------------------------------------------------------------------------
publish_dotnet_image "$ROOT/apps/dispatch/src/Dispatch.Api/Dispatch.Api.csproj" "dispatch-api"

# --- 2. The Quotes API ----------------------------------------------------------------------------
# Docker rather than the SDK, because Day 27 wrote a Dockerfile for the modular monolith and never
# built it — the machine had no daemon at the time. Building it here is the only thing that turns
# that file from an assertion into a fact.
build_docker_image "$ROOT/apps/quotes/backend" "quotes-api-day32"

# --- 3. The broker --------------------------------------------------------------------------------
build_docker_image "$ROOT/apps/quotes/bff" "quotes-bff-day32"

# --- 4. The frontend ------------------------------------------------------------------------------
step "Stamping the broker URL into the frontend bundle"
FRONTEND="$ROOT/apps/quotes/frontend"
ENV_FILE="$FRONTEND/src/environments/environment.production.ts"

# Rewritten rather than hand-edited, so the value in the bundle and the value in the CSP come from
# one place.
#
# The value is `<broker>/api/v1`, and the `/v1` is load-bearing. The Angular client and the broker
# both predate Day 27, when every endpoint moved under a version prefix; the client asks for
# `${baseUrl}/auth/login` and the broker mounts at `/api` and forwards `/api${req.url}`. Left at
# `/api`, the client would ask the broker for `/api/auth/login`, the broker would forward
# `/api/auth/login`, and the API — which serves `/api/v1/auth/login` — would 404 every call.
#
# Putting `/v1` in the client's base URL fixes it without touching either: the client asks for
# `/api/v1/auth/login`, Express strips its `/api` mount, and the broker forwards `/api` + `/v1/...`.
# Two components that never learned about versioning, reconciled by one string.
node -e '
  const fs = require("fs");
  const [file, bff] = process.argv.slice(1);
  const body = fs.readFileSync(file, "utf8");
  const next = body.replace(
    /apiBaseUrl:\s*.+?,/,
    `apiBaseUrl: ${JSON.stringify(bff + "/api/v1")},`
  );
  // Three outcomes, not two. The first version of this check treated "the text did not change"
  // as "the pattern was not found" and aborted the whole build on the second run — when the file
  // was already correct, which is the one case that is definitely fine. A rebuild has to be a
  // no-op, not a failure.
  if (!/apiBaseUrl:/.test(body)) {
    console.error("apiBaseUrl was not found in " + file + " — the file shape changed.");
    process.exit(1);
  }
  if (next === body) {
    console.log("  already correct, nothing to write");
  } else {
    fs.writeFileSync(file, next);
  }
' "$(winpath "$ENV_FILE")" "$BFF_URL"
info "apiBaseUrl = $BFF_URL/api/v1"

build_docker_image "$FRONTEND" "quotes-web-day32"

step "Done"
info "dispatch-api:$IMAGE_TAG"
info "quotes-api-day32:$IMAGE_TAG"
info "quotes-bff-day32:$IMAGE_TAG"
info "quotes-web-day32:$IMAGE_TAG"
printf '\n    Next: bash scripts/deploy.sh\n\n'
