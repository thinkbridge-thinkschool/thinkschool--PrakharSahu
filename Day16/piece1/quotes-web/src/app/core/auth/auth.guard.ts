import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';

import { AccessTokenStore } from './access-token.store';

/**
 * Blocks routes that need a signed-in user, and sends them somewhere useful.
 *
 * Applied ONLY to routes whose underlying endpoint actually requires a token. On the
 * Week-1 API that is the write side:
 *
 *   POST /api/quotes   RequireAuthorization("can-edit-quotes")   QuoteEndpointExtensions.cs:26
 *
 * `GET /api/quotes` and `GET /api/quotes/{id}` are anonymous (no `.RequireAuthorization()`
 * on either — QuoteEndpointExtensions.cs:20 and :23), so guarding them would lock users out
 * of data the server hands to anyone. See exercise.txt for the version of this that got it
 * wrong.
 *
 * Redirects rather than returning `false`: a bare `false` cancels navigation and leaves the
 * user on whatever they were looking at with no explanation, and the URL silently reverts.
 */
export const authGuard: CanActivateFn = (_route, state): boolean | UrlTree => {
  const tokens = inject(AccessTokenStore);
  const router = inject(Router);

  if (tokens.isSignedIn()) {
    return true;
  }

  // `returnUrl` carries the whole attempted URL, params and all, so sign-in can send them
  // back exactly where they were going.
  return router.createUrlTree(['/sign-in'], { queryParams: { returnUrl: state.url } });
};
