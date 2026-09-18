import { DefaultAzureCredential } from '@azure/identity';

import { API_SCOPE, IS_LOCAL } from './config.js';

/**
 * Mints managed-identity access tokens for the Week-1 QuotesApi.
 *
 * `DefaultAzureCredential` walks a chain of credential sources and uses the first that
 * answers. On the Container App that is `ManagedIdentityCredential`, which asks the
 * platform's IMDS endpoint for a token — a request that only succeeds because the
 * Container App has a system-assigned identity and that identity holds an app-role
 * assignment on the API's app registration. On a developer laptop the same chain falls
 * through to `AzureCliCredential` and reuses whoever is signed in to `az`.
 *
 * The important property either way: this process never possesses a long-lived credential.
 * There is nothing to rotate, nothing to leak, and nothing to put in a settings file.
 */
const credential = new DefaultAzureCredential();

/**
 * Last token handed out, kept so a burst of requests does not become a burst of IMDS calls.
 *
 * `@azure/identity` caches internally too, but that cache is per-credential and its refresh
 * policy is not something this file should assume. Re-checking expiry here is cheap and
 * makes the refresh window explicit and testable.
 */
let cached = null;

/**
 * Refresh this long before the token actually expires.
 *
 * Five minutes, because the upstream API validates lifetime with `ClockSkew = TimeSpan.Zero`
 * (InfrastructureExtensions.cs). With zero tolerance on the far side, a token that is
 * "still valid for another few seconds" here can already be expired there, and the failure
 * looks like a random 401 rather than a clock problem.
 */
const REFRESH_MARGIN_MS = 5 * 60 * 1000;

/**
 * Returns a bearer token for the API, reusing the cached one until it is close to expiry.
 *
 * Throws on failure rather than returning null: a proxy that quietly forwards a request
 * without its service credential would turn a misconfigured identity into a confusing 401
 * from the upstream, instead of a clear 503 pointing at this tier.
 */
export async function getCallerToken() {
  const now = Date.now();

  if (cached && cached.expiresOnTimestamp - REFRESH_MARGIN_MS > now) {
    return cached.token;
  }

  const issued = await credential.getToken(API_SCOPE);
  if (!issued?.token) {
    throw new Error(
      `Could not acquire a managed-identity token for ${API_SCOPE}. ` +
        (IS_LOCAL
          ? 'Running locally: check that `az login` is current and that your user has the app role.'
          : 'Check that the Container App has a system-assigned identity and that the identity ' +
            'holds an app-role assignment on the API app registration.'),
    );
  }

  cached = issued;
  return issued.token;
}

/**
 * Decodes the *claims* of the current token for the verification endpoint.
 *
 * Only the payload is read and only non-secret identifiers are returned. None of these are
 * credentials — `tid`, `appid` and `aud` are already sitting in this repo's deploy scripts,
 * and `oid` is the identity's object id, which is meaningless without the ability to
 * authenticate as it. The signature is deliberately never exposed, because the signature
 * plus the payload *is* the token.
 */
export async function describeCallerToken() {
  const token = await getCallerToken();
  const [, payload] = token.split('.');
  const claims = JSON.parse(Buffer.from(payload, 'base64url').toString('utf8'));

  return {
    audience: claims.aud,
    issuer: claims.iss,
    tenantId: claims.tid,
    /** The managed identity's application id — present only on app-only tokens. */
    applicationId: claims.appid ?? claims.azp ?? null,
    /** Object id of the service principal the token was issued to. */
    objectId: claims.oid ?? null,
    /** App roles the identity was granted; empty here means the role assignment is missing. */
    roles: claims.roles ?? [],
    /** Absent on an app-only token. Its absence is the proof no user was involved. */
    subjectIsUser: Boolean(claims.name || claims.preferred_username),
    expiresAt: new Date(claims.exp * 1000).toISOString(),
    /** How the token was obtained, so the verification log can distinguish IMDS from `az`. */
    source: IS_LOCAL ? 'AzureCliCredential (local)' : 'ManagedIdentityCredential (IMDS)',
  };
}
