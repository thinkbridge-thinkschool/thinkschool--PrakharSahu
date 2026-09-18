/**
 * Every value here arrives as an environment variable set on the Container App.
 *
 * Note what is absent: there is no client secret, no certificate, no connection string.
 * The only credential this process ever holds is the one the platform hands it through
 * IMDS, and that one is minted on demand and expires. Nothing here is worth stealing —
 * a tenant id and an application id are public identifiers, not keys.
 */

/** Reads a required variable, failing at startup rather than on the first request. */
function required(name) {
  const value = process.env[name];
  if (!value || value.trim() === '') {
    throw new Error(
      `${name} is not set. The BFF cannot start without it — see Day17/DEPLOY.md.`,
    );
  }
  return value.trim();
}

/** Base URL of the Week-1 QuotesApi, with any trailing slash removed. */
export const UPSTREAM_API_BASE = required('UPSTREAM_API_BASE').replace(/\/+$/, '');

/**
 * The audience the managed-identity token is minted for.
 *
 * `DefaultAzureCredential.getToken` wants a *scope*, and for an app-only token against a
 * custom API that scope is always `<App ID URI>/.default` — the ".default" suffix means
 * "every app role this identity has already been granted", which is the only form that
 * works for a client with no user in the loop. Passing the bare App ID URI silently yields
 * a token the API will reject.
 */
export const API_SCOPE = `${required('API_APP_ID_URI').replace(/\/+$/, '')}/.default`;

/**
 * Origins allowed to call this proxy with credentials.
 *
 * Comma-separated, and never `*`: the browser refuses to send cookies to a wildcard origin,
 * so a wildcard here would break sign-in rather than loosen it.
 */
export const ALLOWED_ORIGINS = required('ALLOWED_ORIGINS')
  .split(',')
  .map((origin) => origin.trim().replace(/\/+$/, ''))
  .filter(Boolean);

export const PORT = Number(process.env.PORT ?? 8080);

/**
 * Set only when running on a developer machine, where there is no IMDS endpoint and
 * `DefaultAzureCredential` falls back to the signed-in `az` user. Off in Azure.
 */
export const IS_LOCAL = process.env.BFF_LOCAL === 'true';
