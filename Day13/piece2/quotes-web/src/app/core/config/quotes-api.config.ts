import { InjectionToken, Provider } from '@angular/core';

/**
 * Base URL that the Week-1 QuotesApi is reachable on, *including* the `/api` segment.
 *
 * The transport layer never hard-codes a host. During `ng serve` this resolves to the
 * relative path `/api`, which the dev-server proxy forwards to http://localhost:5267
 * (see `proxy.conf.json`) — the Week-1 API has no CORS policy, so a cross-origin call
 * straight from http://localhost:4200 is rejected by the browser.
 */
export const QUOTES_API_BASE_URL = new InjectionToken<string>('QUOTES_API_BASE_URL');

/** Registers the API base URL, normalising away any trailing slash. */
export function provideQuotesApiBaseUrl(baseUrl: string): Provider {
  return { provide: QUOTES_API_BASE_URL, useValue: baseUrl.replace(/\/+$/, '') };
}
