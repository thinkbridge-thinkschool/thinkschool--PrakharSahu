import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { QUOTES_API_BASE_URL } from '../config/quotes-api.config';

/**
 * `POST /api/auth/login` — Endpoints/AuthEndpoints.cs:44.
 *
 * Request  `LoginRequest(string Email, string Password)`
 * Response `LoginResponse(string AccessToken, string RefreshToken, int ExpiresIn)`, or 401.
 */
export type SignInOutcome =
  | { readonly status: 'signed-in'; readonly accessToken: string }
  | { readonly status: 'rejected' }
  | { readonly status: 'failed'; readonly message: string };

interface LoginResponseBody {
  readonly accessToken?: unknown;
}

@Injectable({ providedIn: 'root' })
export class AuthApiClient {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(QUOTES_API_BASE_URL);

  /**
   * The credentials are passed straight through and never stored, logged, or echoed back —
   * only the resulting access token leaves this method.
   */
  async signIn(email: string, password: string): Promise<SignInOutcome> {
    try {
      const body = await firstValueFrom(
        this.http.post<unknown>(`${this.baseUrl}/auth/login`, { email, password }),
      );

      const accessToken = (body as LoginResponseBody | null)?.accessToken;
      return typeof accessToken === 'string' && accessToken !== ''
        ? { status: 'signed-in', accessToken }
        : { status: 'failed', message: 'Sign-in succeeded but no access token came back.' };
    } catch (failure) {
      if (failure instanceof HttpErrorResponse && failure.status === 401) {
        return { status: 'rejected' };
      }
      if (failure instanceof HttpErrorResponse && failure.status === 0) {
        return { status: 'failed', message: 'Could not reach the Quotes API on port 5267.' };
      }
      const status = failure instanceof HttpErrorResponse ? failure.status : 'unknown';
      return { status: 'failed', message: `Sign-in failed with status ${status}.` };
    }
  }
}
