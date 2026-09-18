import { Injectable, computed, signal } from '@angular/core';

/**
 * Where the two tokens live.
 *
 * ACCESS TOKEN — a browser cookie, `quotes_at`, written by this file. `Path=/`,
 * `SameSite=Strict`, `Secure`, and `Max-Age` taken from the API's own `expiresIn` (currently
 * 3 hours, `Jwt:ExpiresInSeconds`), so the browser expires it at the same moment the API
 * stops honouring it. Nothing here hardcodes the number — shortening it server-side needs no
 * client change. It is mirrored into a signal so the UI can react to it, but the cookie is
 * the copy that survives a reload.
 *
 * It is deliberately NOT HttpOnly, and it cannot be: the app has to read this value to put
 * it in the `Authorization: Bearer` header on every request. A cookie script can read is a
 * cookie an XSS bug can read — that is the trade being made here, and the token's lifetime
 * plus `SameSite=Strict` is all that bounds it. At 3 hours that bound is six times looser
 * than it was at 30 minutes, which is the cost of the longer session and worth stating
 * plainly rather than burying. (Marking it HttpOnly would hide it from the
 * interceptor too, and the only way to use it would be for the API to accept cookie
 * authentication instead of a bearer header — a server-side change, not a client one.)
 *
 * REFRESH TOKEN — also a cookie, but one this code cannot touch. The API sets it
 * `HttpOnly; Secure; SameSite=Strict; Path=/api/auth` (`AuthEndpoints.cs`,
 * `AppendRefreshCookie`) with a seven-day life. It is never in a response body, never in
 * JavaScript, and is sent automatically only to `/api/auth/*`. That asymmetry is the design:
 * the credential worth stealing is the long-lived one, and it is the one script cannot see.
 *
 * THE HINT — one timestamp in `localStorage` saying "a refresh cookie probably lives until
 * then". Not a credential: it grants nothing, and forging it only buys a wasted request that
 * comes back 401. It exists because the refresh cookie is invisible to this code, so after
 * the access cookie has expired the app would otherwise have no way to know whether a silent
 * refresh is worth attempting.
 */
@Injectable({ providedIn: 'root' })
export class TokenStore {
  private readonly stored = readAccessCookie();

  private readonly token = signal<string | null>(this.stored?.accessToken ?? null);
  private readonly expiry = signal<number | null>(this.stored?.expiresAt ?? null);
  private readonly sessionHint = signal<boolean>(readHint());

  readonly accessToken = this.token.asReadonly();
  readonly expiresAt = this.expiry.asReadonly();

  /** True if a refresh cookie may exist — worth attempting a silent refresh. */
  readonly mayHaveSession = this.sessionHint.asReadonly();

  /**
   * Signed in from the UI's point of view.
   *
   * Deliberately optimistic: after the access cookie expires the refresh cookie usually has
   * days left, so the app shows the signed-in chrome while the silent refresh runs. If the
   * refresh cookie turns out to be dead, the first 401 clears everything.
   */
  readonly isSignedIn = computed(() => this.token() !== null || this.sessionHint());

  /** True once the access token is within a minute of expiry, or already past it. */
  isExpiring(now: number = Date.now()): boolean {
    const expiresAt = this.expiry();
    return expiresAt !== null && expiresAt - now <= 60_000;
  }

  /**
   * Records a fresh access token. The response's `refreshToken` is deliberately ignored —
   * the API returns it empty and delivers the real one as an HttpOnly cookie.
   */
  setAccessToken(accessToken: string, expiresInSeconds: number, now: number = Date.now()): void {
    const expiresAt = now + expiresInSeconds * 1000;
    this.token.set(accessToken);
    this.expiry.set(expiresAt);
    writeAccessCookie(accessToken, expiresAt, expiresInSeconds);
    // Every login AND every refresh re-issues the refresh cookie for another seven days, so
    // the hint slides forward with it. An active user is never asked to sign in again.
    this.setHintUntil(now + REFRESH_LIFETIME_MS);
  }

  /**
   * Drops the access token but keeps the hint, so a silent refresh will still be tried.
   * Used when a token expires rather than when the session ends.
   */
  forgetAccessToken(): void {
    this.token.set(null);
    this.expiry.set(null);
    deleteCookie(ACCESS_COOKIE);
  }

  /** Ends the session locally. The refresh cookie is cleared by the server on logout. */
  clear(): void {
    this.forgetAccessToken();
    this.setHintUntil(null);
  }

  private setHintUntil(until: number | null): void {
    this.sessionHint.set(until !== null);
    writeHint(until);
  }
}

/** Readable by script on purpose — the bearer header is assembled from it. */
export const ACCESS_COOKIE = 'quotes_at';

const HINT_KEY = 'quotes-web.session-until';

/**
 * Must match the cookie's `Expires` in `AuthEndpoints.AppendRefreshCookie` and the
 * `RefreshToken.ExpiresAt` row the API writes — both seven days.
 */
export const REFRESH_LIFETIME_MS = 7 * 24 * 60 * 60 * 1000;

/**
 * The cookie carries the deadline alongside the token, because `document.cookie` exposes
 * only names and values — the browser knows when it expires but will not tell us, and
 * `isExpiring()` needs the number to refresh proactively. `~` is not a base64url or JWT
 * character, so it cannot appear inside the token itself.
 */
const FIELD_SEPARATOR = '~';

interface StoredAccess {
  readonly accessToken: string;
  readonly expiresAt: number;
}

function writeAccessCookie(accessToken: string, expiresAt: number, maxAgeSeconds: number): void {
  // Max-Age, not Expires: it is relative, so a client clock skewed against the server does
  // not hand the user a cookie that is already stale or one that outlives the token.
  setCookie(
    ACCESS_COOKIE,
    `${accessToken}${FIELD_SEPARATOR}${expiresAt}`,
    Math.max(0, Math.floor(maxAgeSeconds)),
  );
}

/** Returns null for an absent, malformed or already-expired cookie — all mean "signed out". */
function readAccessCookie(now: number = Date.now()): StoredAccess | null {
  const raw = getCookie(ACCESS_COOKIE);
  if (raw === null) {
    return null;
  }

  const separator = raw.lastIndexOf(FIELD_SEPARATOR);
  if (separator <= 0) {
    deleteCookie(ACCESS_COOKIE);
    return null;
  }

  const accessToken = raw.slice(0, separator);
  const expiresAt = Number(raw.slice(separator + 1));
  if (!Number.isFinite(expiresAt) || expiresAt <= now) {
    // The browser should already have dropped it; a skewed clock is the case that gets here.
    deleteCookie(ACCESS_COOKIE);
    return null;
  }

  return { accessToken, expiresAt };
}

/**
 * `Secure` even in development: browsers treat `http://localhost` as a trustworthy origin,
 * so the flag costs nothing locally and cannot be forgotten on the way to production.
 */
function setCookie(name: string, value: string, maxAgeSeconds: number): void {
  try {
    if (typeof document === 'undefined') {
      return;
    }
    document.cookie = `${name}=${value}; Max-Age=${maxAgeSeconds}; Path=/; SameSite=Strict; Secure`;
  } catch {
    // Cookies disabled. The session then lasts as long as the tab, which is survivable.
  }
}

function deleteCookie(name: string): void {
  setCookie(name, '', 0);
}

function getCookie(name: string): string | null {
  try {
    if (typeof document === 'undefined') {
      return null;
    }
    const prefix = `${name}=`;
    const match = document.cookie
      .split('; ')
      .find((entry) => entry.startsWith(prefix));
    if (match === undefined) {
      return null;
    }
    const value = match.slice(prefix.length);
    return value === '' ? null : value;
  } catch {
    return null;
  }
}

/**
 * Storage access is guarded: it throws in some privacy modes and is absent outside a
 * browser. An unreadable hint just means the user signs in again.
 */
function readHint(now: number = Date.now()): boolean {
  try {
    if (typeof localStorage === 'undefined') {
      return false;
    }
    const until = Number(localStorage.getItem(HINT_KEY));
    if (!Number.isFinite(until) || until <= now) {
      // Expired or absent. Tidy up so a stale entry does not linger for seven days.
      localStorage.removeItem(HINT_KEY);
      return false;
    }
    return true;
  } catch {
    return false;
  }
}

function writeHint(until: number | null): void {
  try {
    if (typeof localStorage === 'undefined') {
      return;
    }
    if (until === null) {
      localStorage.removeItem(HINT_KEY);
    } else {
      localStorage.setItem(HINT_KEY, String(until));
    }
  } catch {
    // Without the hint the app simply asks the user to sign in again.
  }
}
