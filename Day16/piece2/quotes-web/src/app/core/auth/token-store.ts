import { Injectable, computed, signal } from '@angular/core';

/**
 * Where the two tokens live, and why they live in different places.
 *
 * ACCESS TOKEN — an ordinary in-memory variable (held in a signal so the UI can react to
 * it). Never written to localStorage, sessionStorage or a cookie. It dies with the tab, and
 * an XSS bug can only reach it while the page is open. It is short-lived anyway: one hour,
 * per `expiresIn` from the API.
 *
 * REFRESH TOKEN — not here at all. The API sets it as an `HttpOnly; Secure; SameSite=Strict`
 * cookie scoped to `/api/auth` (`AuthEndpoints.cs`, `AppendRefreshCookie`). JavaScript
 * cannot read an HttpOnly cookie, which is the entire point: the long-lived credential is
 * unreachable from application code, so no amount of script injection can exfiltrate it.
 * A cookie written from JavaScript could never have that flag, which is why this had to be
 * done on the server.
 *
 * THE HINT — `sessionStorage` holds one boolean, "a refresh cookie probably exists". That
 * is not a credential and grants nothing; it exists because the app cannot see the cookie
 * and would otherwise not know whether attempting a silent refresh on boot is worthwhile.
 */
@Injectable({ providedIn: 'root' })
export class TokenStore {
  /** The access token. A plain variable — nothing persists it. */
  private readonly token = signal<string | null>(null);
  private readonly expiry = signal<number | null>(null);

  private readonly sessionHint = signal<boolean>(readHint());

  readonly accessToken = this.token.asReadonly();
  readonly expiresAt = this.expiry.asReadonly();

  /** True if a refresh cookie may exist — worth attempting a silent refresh. */
  readonly mayHaveSession = this.sessionHint.asReadonly();

  /**
   * Signed in from the UI's point of view.
   *
   * Deliberately optimistic after a reload: the access token is gone but the refresh cookie
   * is not, so the app shows the signed-in chrome while the silent refresh runs. If the
   * cookie turns out to be dead, the first 401 clears everything.
   */
  readonly isSignedIn = computed(() => this.token() !== null || this.sessionHint());

  /** True once the access token is within a minute of expiry, or already past it. */
  isExpiring(now: number = Date.now()): boolean {
    const expiresAt = this.expiry();
    return expiresAt !== null && expiresAt - now <= 60_000;
  }

  /**
   * Records a fresh access token. The response's `refreshToken` is deliberately ignored —
   * the API now returns it empty and delivers the real one as a cookie.
   */
  setAccessToken(accessToken: string, expiresInSeconds: number, now: number = Date.now()): void {
    this.token.set(accessToken);
    this.expiry.set(now + expiresInSeconds * 1000);
    this.setHint(true);
  }

  /**
   * Forgets the access token but keeps the hint, so a silent refresh will still be tried.
   * Used when a token expires rather than when the session ends.
   */
  forgetAccessToken(): void {
    this.token.set(null);
    this.expiry.set(null);
  }

  /** Ends the session locally. The cookie itself is cleared by the server on logout. */
  clear(): void {
    this.token.set(null);
    this.expiry.set(null);
    this.setHint(false);
  }

  private setHint(value: boolean): void {
    this.sessionHint.set(value);
    writeHint(value);
  }
}

const HINT_KEY = 'quotes-web.has-session';

/** Storage access is guarded: it throws in some privacy modes and is absent outside a browser. */
function readHint(): boolean {
  try {
    return typeof sessionStorage !== 'undefined' && sessionStorage.getItem(HINT_KEY) === '1';
  } catch {
    return false;
  }
}

function writeHint(value: boolean): void {
  try {
    if (typeof sessionStorage === 'undefined') {
      return;
    }
    if (value) {
      sessionStorage.setItem(HINT_KEY, '1');
    } else {
      sessionStorage.removeItem(HINT_KEY);
    }
  } catch {
    // Without the hint the app simply asks the user to sign in again. Not worth failing over.
  }
}
