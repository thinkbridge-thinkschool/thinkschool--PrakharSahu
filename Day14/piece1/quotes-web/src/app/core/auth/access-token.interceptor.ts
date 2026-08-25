import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

import { AccessTokenStore } from './access-token.store';

/** Requests that must never carry a bearer token — sending one to login is meaningless. */
const UNAUTHENTICATED_PATHS = ['/auth/login', '/auth/refresh'];

/**
 * Attaches `Authorization: Bearer <token>` to API calls once a token exists.
 *
 * Reads the token at request time rather than at construction, so a sign-in mid-session
 * takes effect on the very next request without anything being re-created.
 */
export const accessTokenInterceptor: HttpInterceptorFn = (request, next) => {
  const accessToken = inject(AccessTokenStore).accessToken();

  const skip =
    accessToken === null || UNAUTHENTICATED_PATHS.some((path) => request.url.includes(path));

  return skip
    ? next(request)
    : next(request.clone({ setHeaders: { Authorization: `Bearer ${accessToken}` } }));
};
