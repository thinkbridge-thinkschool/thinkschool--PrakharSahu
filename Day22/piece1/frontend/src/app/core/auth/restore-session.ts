import { EnvironmentProviders, inject, provideAppInitializer } from '@angular/core';

import { TokenRefresher } from './refresh-on-401.interceptor';
import { TokenStore } from './token-store';

/**
 * Puts the app back into a signed-in state at startup, at the lowest cost the situation
 * allows.
 *
 * Three cases, in order of how much they cost:
 *   - the access cookie is still alive        nothing to do; `TokenStore` already read it.
 *   - it is gone or nearly gone, but the hint says a refresh cookie should still exist
 *                                             one request to `/api/auth/refresh`, which the
 *                                             browser answers with the HttpOnly cookie.
 *   - no hint                                 a first-time or signed-out visitor. Skipped
 *                                             entirely, so they do not pay for a doomed 401.
 *
 * It never blocks bootstrap in a way the user can fail: the promise is awaited, but a failed
 * refresh just leaves them signed out, and `TokenRefresher` clears the hint so the next
 * reload does not try again.
 */
export function provideSessionRestore(): EnvironmentProviders {
  return provideAppInitializer(async () => {
    const tokens = inject(TokenStore);

    if (!tokens.mayHaveSession()) {
      return;
    }
    // A token with under a minute left is refreshed now rather than mid-click. The null
    // check has to come first: `isExpiring()` is false when there is no token, since it
    // answers "is this token nearly done", not "do we need one".
    if (tokens.accessToken() !== null && !tokens.isExpiring()) {
      return;
    }

    await inject(TokenRefresher).refresh();
  });
}
