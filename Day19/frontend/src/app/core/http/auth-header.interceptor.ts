import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

import { TokenStore } from '../auth/token-store';

/** Endpoints that must never receive a bearer token. Sending one to login is meaningless. */
const UNAUTHENTICATED_PATHS = ['/auth/login', '/auth/refresh'];

/**
 * Attaches `Authorization: Bearer <token>` once a token exists.
 *
 * Reads the token at request time rather than at construction, so signing in mid-session
 * affects the very next request without anything being rebuilt. An existing `Authorization`
 * header is left alone — a caller that set one deliberately outranks this.
 */
export const authHeaderInterceptor: HttpInterceptorFn = (request, next) => {
  const accessToken = inject(TokenStore).accessToken();

  const skip =
    accessToken === null ||
    request.headers.has('Authorization') ||
    UNAUTHENTICATED_PATHS.some((path) => request.url.includes(path));

  return skip
    ? next(request)
    : next(request.clone({ setHeaders: { Authorization: `Bearer ${accessToken}` } }));
};
