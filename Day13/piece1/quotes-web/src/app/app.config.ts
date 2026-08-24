import { provideHttpClient, withFetch } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';

import { provideQuotesApiBaseUrl } from './core/config/quotes-api.config';
import { routes } from './app.routes';

/**
 * Root providers.
 *
 * There is deliberately no `provideZoneChangeDetection()` here: Angular 21 is zoneless by
 * default, `zone.js` is not a dependency, and `angular.json` declares no polyfills. Change
 * detection is driven by signal writes, so every component in this app is `OnPush`.
 */
export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withFetch()),
    // Relative on purpose — `ng serve` proxies /api to http://localhost:5267 (proxy.conf.json).
    provideQuotesApiBaseUrl('/api'),
  ],
};
