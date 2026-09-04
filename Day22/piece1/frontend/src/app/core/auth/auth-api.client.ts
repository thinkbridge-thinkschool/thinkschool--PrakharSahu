import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { QUOTES_API_BASE_URL } from '../config/quotes-api.config';
import { ApiError } from '../http/api-error';

/**
 * What the auth endpoints return to a browser.
 *
 * `refreshToken` is intentionally absent: the API now delivers it as an HttpOnly cookie and
 * sends an empty string in the body. Modelling it here would invite somebody to start
 * reading it again.
 */
export interface AccessTokenResponse {
  readonly accessToken: string;
  readonly expiresIn: number;
}

export type SignInOutcome =
  | { readonly status: 'signed-in'; readonly tokens: AccessTokenResponse }
  | { readonly status: 'rejected'; readonly message: string };

/**
 * Registration succeeds into the same shape as sign-in, because the API signs the new account
 * in as part of creating it — one round trip, and no window in which the account exists but
 * the user is still looking at a login form.
 */
export type RegisterOutcome = SignInOutcome;

/**
 * `withCredentials` on every auth call: the refresh cookie has to be attached on the way
 * out and stored on the way back. Without it the cookie is silently ignored and refresh
 * fails with a 401 that looks like a bad token.
 */
const WITH_COOKIES = { withCredentials: true } as const;

@Injectable({ providedIn: 'root' })
export class AuthApiClient {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(QUOTES_API_BASE_URL);

  /** Credentials pass straight through and are never stored, logged, or echoed back. */
  async signIn(email: string, password: string): Promise<SignInOutcome> {
    try {
      const body = await firstValueFrom(
        this.http.post<unknown>(`${this.baseUrl}/auth/login`, { email, password }, WITH_COOKIES),
      );
      const tokens = asAccessToken(body);
      return tokens
        ? { status: 'signed-in', tokens }
        : { status: 'rejected', message: 'Sign-in succeeded but the response was unreadable.' };
    } catch (failure) {
      const message =
        failure instanceof ApiError
          ? failure.kind === 'unauthorized'
            ? 'That email and password combination was not accepted.'
            : failure.friendlyMessage
          : 'Sign-in failed.';
      return { status: 'rejected', message };
    }
  }

  /**
   * `POST /api/auth/register` — creates the account and returns a live session.
   *
   * The API answers 201 with the same body as login, so a new user lands signed in rather
   * than being bounced to a sign-in form to retype what they just typed. A 409 means the
   * email is taken and a 400 carries a validation problem; both are turned into a sentence
   * the form can show, because "Request failed with status code 409" is not one.
   */
  async register(email: string, password: string): Promise<RegisterOutcome> {
    try {
      const body = await firstValueFrom(
        this.http.post<unknown>(`${this.baseUrl}/auth/register`, { email, password }, WITH_COOKIES),
      );
      const tokens = asAccessToken(body);
      return tokens
        ? { status: 'signed-in', tokens }
        : { status: 'rejected', message: 'The account was created but the response was unreadable.' };
    } catch (failure) {
      return { status: 'rejected', message: explainRegistrationFailure(failure) };
    }
  }

  /**
   * `POST /api/auth/refresh` — the request carries NO token.
   *
   * The browser attaches the HttpOnly cookie; the server reads it from `Request.Cookies`
   * and rotates it in the response. Application code never sees either value.
   *
   * Rotation is enforced: presenting a spent token returns 401 *and* revokes the whole
   * token family (`AuthEndpoints.cs`, "SECURITY ALERT"), which is why the caller must never
   * let two refreshes run at once.
   */
  async refresh(): Promise<AccessTokenResponse | null> {
    try {
      const body = await firstValueFrom(
        this.http.post<unknown>(`${this.baseUrl}/auth/refresh`, {}, WITH_COOKIES),
      );
      return asAccessToken(body);
    } catch {
      return null;
    }
  }

  /** Revokes the token server-side and clears the cookie. Best effort. */
  async signOut(): Promise<void> {
    try {
      await firstValueFrom(this.http.post(`${this.baseUrl}/auth/logout`, {}, WITH_COOKIES));
    } catch {
      // Already signed out, or the token was dead. Local state is cleared either way.
    }
  }
}

/**
 * Turns a failed registration into one sentence a form can display.
 *
 * `ApiError.friendlyMessage` is already good for a 409 — the API sends
 * `{ "message": "An account with that email already exists." }` and the error mapper lifts it
 * out. A 400 is the exception: ASP.NET's `ValidationProblem` sets the title to the useless
 * "One or more validation errors occurred." and puts the real reason in `errors`, so the
 * field message is preferred whenever there is one.
 */
function explainRegistrationFailure(failure: unknown): string {
  if (!(failure instanceof ApiError)) {
    return 'Could not create the account.';
  }

  const firstFieldMessage = [...failure.fieldErrors.values()].flat()[0];
  if (failure.kind === 'validation' && firstFieldMessage !== undefined) {
    return firstFieldMessage;
  }

  return failure.friendlyMessage;
}

function asAccessToken(body: unknown): AccessTokenResponse | null {
  if (typeof body !== 'object' || body === null) {
    return null;
  }
  const candidate = body as Record<string, unknown>;
  const { accessToken, expiresIn } = candidate;

  return typeof accessToken === 'string' && accessToken !== '' && typeof expiresIn === 'number'
    ? { accessToken, expiresIn }
    : null;
}
