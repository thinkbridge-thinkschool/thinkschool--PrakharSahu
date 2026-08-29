import { HttpInterceptorFn, provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import {
  provideRouter,
  withComponentInputBinding,
  withInMemoryScrolling,
  withViewTransitions,
} from '@angular/router';

import { environment } from '../environments/environment';
import { routes } from './app.routes';
import { provideQuotesApiBaseUrl } from './core/config/quotes-api.config';
import { authHeaderInterceptor } from './core/http/auth-header.interceptor';
import { refreshOn401Interceptor } from './core/auth/refresh-on-401.interceptor';
import { provideSessionRestore } from './core/auth/restore-session';
import { errorMappingInterceptor } from './core/http/error-mapping.interceptor';
import { retryIdempotentInterceptor } from './core/http/retry-idempotent.interceptor';

/**
 * Interceptor order, exported so tests assert the real configuration rather than a copy.
 *
 * Angular builds the chain in array order: `[A, B, C]` becomes `A(B(C(backend)))`. Requests
 * travel A → B → C → backend; responses and errors come back C → B → A. So the LAST entry
 * is the one closest to the network.
 *
 *   authHeader      outermost — stamps the token once, before anything replays the request
 *   errorMapping    middle    — maps whatever finally fails, after retries are exhausted
 *   retryIdempotent innermost — re-issues the real request and sees raw HttpErrorResponses
 *
 * Swapping the last two is the trap: retry would then receive an already-mapped `ApiError`,
 * `error instanceof HttpErrorResponse` would be false, and nothing would ever be retried.
 * `interceptor-order.spec.ts` pins this.
 */
export const HTTP_INTERCEPTOR_CHAIN: readonly HttpInterceptorFn[] = [
  authHeaderInterceptor,
  errorMappingInterceptor,
  // Below the mapper so it sees a raw 401, above retry so a refreshed request is re-issued
  // through the real backend. It re-stamps the Authorization header itself, because
  // replaying from here bypasses authHeaderInterceptor.
  refreshOn401Interceptor,
  retryIdempotentInterceptor,
];

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(
      routes,
      // Cross-document-style animation between list and detail, driven by the browser's
      // View Transitions API. Angular only wraps the navigation; the pairing is done in CSS
      // with matching `view-transition-name`s. Unsupported browsers just navigate normally.
      withViewTransitions(),
      // `:id` arrives as a component input instead of being read from ActivatedRoute.
      withComponentInputBinding(),
      withInMemoryScrolling({ scrollPositionRestoration: 'enabled', anchorScrolling: 'enabled' }),
    ),
    provideHttpClient(withFetch(), withInterceptors([...HTTP_INTERCEPTOR_CHAIN])),
    // Relative in development, where `ng serve` proxies /api to http://localhost:5267.
    // Absolute in production, pointing at the BFF on Container Apps — the deployed browser
    // and the API are on different sites, so there is nothing to be relative to. The API's
    // own address is never shipped to the browser; only the BFF can reach it.
    provideQuotesApiBaseUrl(environment.apiBaseUrl),
    // Trades the HttpOnly refresh cookie for an access token on boot, so a reload keeps
    // the session without any credential living in JavaScript-readable storage.
    provideSessionRestore(),
  ],
};
