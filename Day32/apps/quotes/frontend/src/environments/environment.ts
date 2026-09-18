/**
 * Development configuration.
 *
 * `/api` stays relative so `ng serve` can proxy it to the local API on port 5267
 * (proxy.conf.json) and the browser never makes a cross-origin request. Nothing about the
 * deployed topology leaks into local development.
 */
export const environment = {
  production: false,
  /** Base URL of the Quotes API, including the `/api` segment and no trailing slash. */
  apiBaseUrl: '/api',
} as const;
