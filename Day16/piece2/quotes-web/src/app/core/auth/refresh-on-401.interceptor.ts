import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { Injectable, Injector, inject } from '@angular/core';
import { catchError, from, switchMap, throwError } from 'rxjs';

import { AuthApiClient } from './auth-api.client';
import { TokenStore } from './token-store';

/** Requests that must never trigger a refresh — refreshing because refresh failed is a loop. */
const AUTH_PATHS = ['/auth/login', '/auth/refresh', '/auth/logout'];

/**
 * Serialises refreshes across the whole app.
 *
 * The API rotates refresh tokens and treats a REUSED one as an attack: 401 plus revocation
 * of the entire token family (`AuthEndpoints.cs` — "SECURITY ALERT: Attempted reuse of
 * revoked refresh token"). Two requests 401-ing at the same moment must therefore produce
 * ONE refresh, or the second presents a cookie the first already spent and the user is
 * hard-signed-out. One in-flight promise, shared by every caller.
 */
@Injectable({ providedIn: 'root' })
export class TokenRefresher {
  private readonly auth = inject(AuthApiClient);
  private readonly tokens = inject(TokenStore);

  private inFlight: Promise<string | null> | null = null;

  /** Resolves with a fresh access token, or null if the session is finished. */
  refresh(): Promise<string | null> {
    this.inFlight ??= this.runRefresh().finally(() => {
      this.inFlight = null;
    });
    return this.inFlight;
  }

  private async runRefresh(): Promise<string | null> {
    // No token is passed: the browser sends the HttpOnly cookie for us.
    const next = await this.auth.refresh();

    if (next === null) {
      // The cookie is spent, revoked, expired or absent. Nothing to salvage.
      this.tokens.clear();
      return null;
    }

    this.tokens.setAccessToken(next.accessToken, next.expiresIn);
    return next.accessToken;
  }
}

/**
 * Retries a request once with a fresh access token after a 401.
 *
 * Gated on `mayHaveSession` rather than on a stored refresh token, because the refresh
 * token is an HttpOnly cookie this code cannot see. That also makes a reload work: the
 * access token is gone, the cookie is not, and the first 401 quietly re-authenticates.
 *
 * Sits BELOW the error mapper so it sees a raw `HttpErrorResponse`, and re-stamps the
 * Authorization header itself — replaying through `next()` from here bypasses
 * `authHeaderInterceptor`, so the retry would otherwise carry the dead token.
 */
export const refreshOn401Interceptor: HttpInterceptorFn = (request, next) => {
  const tokens = inject(TokenStore);
  // Resolved lazily: `TokenRefresher` pulls in `AuthApiClient`, which needs
  // `QUOTES_API_BASE_URL`. Injecting eagerly would make every request in the app depend on
  // the whole auth stack even when no refresh is possible.
  const injector = inject(Injector);

  if (AUTH_PATHS.some((path) => request.url.includes(path)) || !tokens.mayHaveSession()) {
    return next(request);
  }

  return next(request).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401) {
        return throwError(() => error);
      }

      return from(injector.get(TokenRefresher).refresh()).pipe(
        switchMap((accessToken) =>
          accessToken === null
            ? throwError(() => error) // session is gone; surface the original 401
            : next(withBearer(request, accessToken)),
        ),
      );
    }),
  );
};

function withBearer(request: HttpRequest<unknown>, accessToken: string): HttpRequest<unknown> {
  return request.clone({ setHeaders: { Authorization: `Bearer ${accessToken}` } });
}
