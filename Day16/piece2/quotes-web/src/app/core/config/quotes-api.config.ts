import { InjectionToken, Provider } from '@angular/core';

/**
 * Base URL of the Week-1 QuotesApi, including the `/api` segment.
 *
 * Relative during development: the API sends no `Access-Control-Allow-Origin`, so the dev
 * server proxies `/api` to http://localhost:5267 (see proxy.conf.json) and the browser
 * never makes a cross-origin request.
 */
export const QUOTES_API_BASE_URL = new InjectionToken<string>('QUOTES_API_BASE_URL');

export function provideQuotesApiBaseUrl(baseUrl: string): Provider {
  return { provide: QUOTES_API_BASE_URL, useValue: baseUrl.replace(/\/+$/, '') };
}
