import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiError } from '../../../core/http/api-error';
import { QUOTES_API_BASE_URL } from '../../../core/config/quotes-api.config';

/**
 * `POST /api/auth/login` — AuthEndpoints.cs:44.
 *
 * Request  `LoginRequest(string Email, string Password)`
 * Response `LoginResponse(string AccessToken, string RefreshToken, int ExpiresIn)`, or 401
 *          with an empty body.
 */
export type SignInOutcome =
  | { readonly status: 'signed-in'; readonly accessToken: string }
  | { readonly status: 'rejected'; readonly message: string };

@Injectable({ providedIn: 'root' })
export class AuthApiClient {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(QUOTES_API_BASE_URL);

  /** Credentials pass straight through and are never stored, logged, or echoed back. */
  async signIn(email: string, password: string): Promise<SignInOutcome> {
    try {
      const body = await firstValueFrom(
        this.http.post<{ accessToken?: unknown }>(`${this.baseUrl}/auth/login`, { email, password }),
      );
      const accessToken = body?.accessToken;
      return typeof accessToken === 'string' && accessToken !== ''
        ? { status: 'signed-in', accessToken }
        : { status: 'rejected', message: 'Sign-in succeeded but no access token came back.' };
    } catch (failure) {
      // The interceptor already turned this into an ApiError with a friendly message.
      const message =
        failure instanceof ApiError
          ? failure.kind === 'unauthorized'
            ? 'That email and password combination was not accepted.'
            : failure.friendlyMessage
          : 'Sign-in failed.';
      return { status: 'rejected', message };
    }
  }
}
