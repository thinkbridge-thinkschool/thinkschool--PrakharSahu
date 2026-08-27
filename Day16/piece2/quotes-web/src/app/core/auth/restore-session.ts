import { EnvironmentProviders, inject, provideAppInitializer } from '@angular/core';

import { TokenRefresher } from './refresh-on-401.interceptor';
import { TokenStore } from './token-store';

/**
 * Trades the refresh cookie for an access token once, at startup.
 *
 * The access token is a plain in-memory variable, so a reload loses it. The HttpOnly cookie
 * survives, and this is what turns that cookie back into a usable session — without any
 * credential ever having been readable by application code.
 *
 * Only runs when the hint says a session probably exists, so a first-time visitor does not
 * pay for a doomed request. It never blocks bootstrap: the promise is awaited, but a
 * failure just leaves the user signed out, and `TokenRefresher` clears the hint so the next
 * reload does not try again.
 */
export function provideSessionRestore(): EnvironmentProviders {
  return provideAppInitializer(async () => {
    const tokens = inject(TokenStore);
    if (!tokens.mayHaveSession()) {
      return;
    }
    await inject(TokenRefresher).refresh();
  });
}
