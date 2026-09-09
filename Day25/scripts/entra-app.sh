#!/usr/bin/env bash
#
# Day 25 — create the Entra app registration that fronts the API.
#
#   ./scripts/entra-app.sh
#
# Idempotent. Re-running finds the existing registration and reconciles it.
#
# ---------------------------------------------------------------------------------------------
# WHY THIS IS A SCRIPT AND NOT BICEP
#
# An app registration is a Microsoft GRAPH object, not an Azure resource. It lives in the Entra
# directory, which sits beside the subscription rather than inside it, and ARM cannot create,
# read or delete one. Bicep extensibility for Graph exists in preview and is not used here.
#
# So this is a genuine seam in "everything is infrastructure as code", and pretending otherwise
# would be the dishonest part. What can be done is to make the script idempotent and to have it
# emit the one value the template needs — a client id, which is public.
#
# ---------------------------------------------------------------------------------------------
# WHAT IT CREATES, AND WHAT IT DELIBERATELY DOES NOT
#
#   an app registration          identity of the API in Entra
#   an Application ID URI        api://<clientId> — the audience tokens must target
#   one delegated scope          user_impersonation, so a token can be requested at all
#   a service principal          the directory-local object without which the tenant will not
#                                issue tokens for this application
#
#   NO CLIENT SECRET             none is created and none is needed. The API validates tokens;
#   NO CERTIFICATE               it never exchanges an authorization code, and the exchange is
#                                the only step that requires the app to prove it is itself.
#                                `az ad app credential reset` is the command NOT run here.
# ---------------------------------------------------------------------------------------------
set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.." || exit 1

APP_NAME="${APP_NAME:-identity-e2e-api}"

# The Azure CLI's own well-known public client id, identical in every tenant. Pre-authorising it
# is what allows `az account get-access-token --resource api://<id>` to return a token for this
# API without an interactive consent prompt — which is what makes the end-to-end proof runnable
# from a terminal rather than a browser.
AZ_CLI_CLIENT_ID='04b07795-8ddb-461a-bbee-02f9e1bf7b46'

die() { printf '\n%s\n' "$*" >&2; exit 1; }

az account show >/dev/null 2>&1 || die "Not signed in. Run 'az login'."
TENANT_ID="$(az account show --query tenantId -o tsv)"

echo "=== Entra app registration: ${APP_NAME} ==="

# ---- find or create --------------------------------------------------------------------------
APP_ID="$(az ad app list --display-name "$APP_NAME" --query "[0].appId" -o tsv 2>/dev/null)"

if [ -n "$APP_ID" ] && [ "$APP_ID" != "null" ]; then
  echo "  found existing registration: $APP_ID"
else
  echo "  creating..."
  APP_ID="$(az ad app create --display-name "$APP_NAME" --sign-in-audience AzureADMyOrg \
    --query appId -o tsv 2>/dev/null)" \
    || die "Could not create the app registration. This directory may not permit it."
  [ -n "$APP_ID" ] || die "App creation returned no client id."
  echo "  created: $APP_ID"
fi

OBJECT_ID="$(az ad app show --id "$APP_ID" --query id -o tsv)" || die "Could not read the registration back."

# ---------------------------------------------------------------------------------------------
# The Application ID URI and the delegated scope.
#
# Both are needed before Entra will mint a token whose `aud` claim is this API. Without the
# identifier URI there is no audience to request; without at least one scope the tenant refuses
# to issue a token for the resource at all, with an error that does not mention scopes.
#
# The scope id must be a STABLE guid — regenerating it on every run would invalidate consent
# already granted, so it is derived deterministically from the app id.
# ---------------------------------------------------------------------------------------------
SCOPE_ID="$(python -c "
import uuid, sys
print(uuid.uuid5(uuid.NAMESPACE_URL, 'identity-e2e/' + sys.argv[1]))
" "$APP_ID")"

echo "  setting identifier URI and delegated scope"

az rest --method PATCH \
  --uri "https://graph.microsoft.com/v1.0/applications/${OBJECT_ID}" \
  --headers 'Content-Type=application/json' \
  --body "{
    \"identifierUris\": [\"api://${APP_ID}\"],
    \"api\": {
      \"requestedAccessTokenVersion\": 2,
      \"oauth2PermissionScopes\": [
        {
          \"id\": \"${SCOPE_ID}\",
          \"adminConsentDescription\": \"Allow the application to access the identity API on behalf of the signed-in user.\",
          \"adminConsentDisplayName\": \"Access the identity API\",
          \"userConsentDescription\": \"Allow the application to access the identity API on your behalf.\",
          \"userConsentDisplayName\": \"Access the identity API\",
          \"value\": \"user_impersonation\",
          \"type\": \"User\",
          \"isEnabled\": true
        }
      ]
    }
  }" >/dev/null 2>&1 || echo "  NOTE: could not set the identifier URI (it may already be set)."

# ---- pre-authorise the Azure CLI so a token can be fetched non-interactively ------------------
echo "  pre-authorising the Azure CLI client for that scope"
az rest --method PATCH \
  --uri "https://graph.microsoft.com/v1.0/applications/${OBJECT_ID}" \
  --headers 'Content-Type=application/json' \
  --body "{
    \"api\": {
      \"requestedAccessTokenVersion\": 2,
      \"preAuthorizedApplications\": [
        {
          \"appId\": \"${AZ_CLI_CLIENT_ID}\",
          \"delegatedPermissionIds\": [\"${SCOPE_ID}\"]
        }
      ]
    }
  }" >/dev/null 2>&1 || echo "  NOTE: could not pre-authorise the CLI; a token may need a browser consent."

# ---------------------------------------------------------------------------------------------
# The service principal.
#
# An application object is the global DEFINITION; a service principal is its instance in this
# directory. Entra issues tokens to and for service principals, so without one the registration
# exists and every token request fails with a message about the application not being found in
# the directory — which reads as if the registration itself were missing.
# ---------------------------------------------------------------------------------------------
if az ad sp show --id "$APP_ID" >/dev/null 2>&1; then
  echo "  service principal already exists"
else
  echo "  creating the service principal"
  az ad sp create --id "$APP_ID" -o none 2>/dev/null \
    || echo "  NOTE: could not create the service principal."
fi

# ---- what came out ---------------------------------------------------------------------------
cat <<EOF

=== Done ===

  ENTRA_CLIENT_ID   ${APP_ID}
  audience          api://${APP_ID}
  tenant            ${TENANT_ID}
  scope             api://${APP_ID}/user_impersonation

None of the above is a secret. A client id is designed to be public — every single-page app that
signs in with Entra ships one in its JavaScript bundle. No client secret and no certificate was
created, because this API validates tokens rather than issuing them.

Export it for the deployment:

  export ENTRA_CLIENT_ID=${APP_ID}

EOF
