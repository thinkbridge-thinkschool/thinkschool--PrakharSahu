import { TestBed } from '@angular/core/testing';

import { ACCESS_COOKIE, REFRESH_LIFETIME_MS, TokenStore } from './token-store';

import { clearBrowserState } from '../../../testing/browser-state';

const HINT_KEY = 'quotes-web.session-until';

/** What the API actually sends as `expiresIn` — `Jwt:ExpiresInSeconds`, three hours. */
const ACCESS_LIFETIME_SECONDS = 10_800;

/** Reads a cookie the way the browser presents it, without the store's parsing. */
function rawCookie(name: string): string | null {
  const prefix = `${name}=`;
  const match = document.cookie.split('; ').find((entry) => entry.startsWith(prefix));
  return match === undefined || match === prefix ? null : match.slice(prefix.length);
}

describe('TokenStore', () => {
  function freshStore(): TokenStore {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({});
    return TestBed.inject(TokenStore);
  }

  beforeEach(() => {
    clearBrowserState();
  });

  it('starts signed out', () => {
    const store = freshStore();

    expect(store.isSignedIn()).toBe(false);
    expect(store.accessToken()).toBeNull();
    expect(store.mayHaveSession()).toBe(false);
  });

  it('holds the access token and its deadline', () => {
    const store = freshStore();
    store.setAccessToken('at', ACCESS_LIFETIME_SECONDS, 1_000);

    expect(store.accessToken()).toBe('at');
    expect(store.expiresAt()).toBe(1_000 + ACCESS_LIFETIME_SECONDS * 1000);
  });

  describe('where the access token is kept', () => {
    it('goes into the quotes_at cookie and nowhere else', () => {
      freshStore().setAccessToken('an-access-token', ACCESS_LIFETIME_SECONDS);

      expect(rawCookie(ACCESS_COOKIE)).toContain('an-access-token');

      // A cookie is the decision; Web Storage is not. Keeping both would be two copies of
      // one credential with two lifetimes, and the localStorage one would outlive the token.
      const webStorage = [
        ...Object.keys(sessionStorage).map((k) => sessionStorage.getItem(k) ?? ''),
        ...Object.keys(localStorage).map((k) => localStorage.getItem(k) ?? ''),
      ].join('|');
      expect(webStorage).not.toContain('an-access-token');
    });

    /**
     * The consequence of putting it in a cookie, stated as a test rather than left implicit:
     * this cookie is script-readable, and has to be, because the interceptor builds the
     * `Authorization: Bearer` header from it. An HttpOnly access cookie would be invisible
     * to the interceptor too and would only work if the API accepted cookie auth.
     */
    it('is readable by script, which is the trade being made', () => {
      freshStore().setAccessToken('an-access-token', ACCESS_LIFETIME_SECONDS);

      expect(document.cookie).toContain('an-access-token');
    });

    it('survives a reload, so a refresh round-trip is not needed on every boot', () => {
      freshStore().setAccessToken('an-access-token', ACCESS_LIFETIME_SECONDS);

      const reloaded = freshStore();

      expect(reloaded.accessToken()).toBe('an-access-token');
      expect(reloaded.isSignedIn()).toBe(true);
    });

    it('carries its own deadline, because document.cookie will not report expiry', () => {
      // Must be a real "now": a deadline in the past is discarded on read, which is the
      // next test. Pinning this to a fixed 2023 timestamp made it assert that instead.
      const now = Date.now();
      freshStore().setAccessToken('an-access-token', ACCESS_LIFETIME_SECONDS, now);

      expect(freshStore().expiresAt()).toBe(now + ACCESS_LIFETIME_SECONDS * 1000);
    });

    it('is dropped, not trusted, when the stored deadline has already passed', () => {
      // A clock skewed forward leaves a cookie the browser has not expired but the API will
      // reject. Reading it back would put the app in a signed-in state with a dead token.
      document.cookie = `${ACCESS_COOKIE}=stale-token~1; Max-Age=600; Path=/`;

      const store = freshStore();

      expect(store.accessToken()).toBeNull();
      expect(rawCookie(ACCESS_COOKIE)).toBeNull();
    });

    it('is dropped when the cookie is malformed rather than parsed into a broken token', () => {
      document.cookie = `${ACCESS_COOKIE}=no-separator-here; Max-Age=600; Path=/`;

      expect(freshStore().accessToken()).toBeNull();
    });

    it('is removed from the browser by clear()', () => {
      const store = freshStore();
      store.setAccessToken('an-access-token', ACCESS_LIFETIME_SECONDS);

      store.clear();

      expect(rawCookie(ACCESS_COOKIE)).toBeNull();
      expect(freshStore().accessToken()).toBeNull();
    });

    it('is removed by forgetAccessToken() too, which only drops the short-lived half', () => {
      const store = freshStore();
      store.setAccessToken('an-access-token', ACCESS_LIFETIME_SECONDS);

      store.forgetAccessToken();

      expect(rawCookie(ACCESS_COOKIE)).toBeNull();
      expect(store.mayHaveSession()).toBe(true);
    });
  });

  it('exposes no refresh token at all — it lives in an HttpOnly cookie', () => {
    const store = freshStore() as unknown as Record<string, unknown>;

    expect(store['refreshToken']).toBeUndefined();
    // Set by the API, invisible here. If this ever reads back a value, the server has
    // dropped HttpOnly and the long-lived credential is exposed to script.
    expect(document.cookie).not.toContain('quotes_rt');
  });

  describe('the session hint', () => {
    /**
     * The hint outlives the access cookie by design. Three hours in, the access cookie is
     * gone and only the hint says the seven-day refresh cookie is still worth trying.
     */
    it('keeps the session recoverable after the access cookie has expired', () => {
      const store = freshStore();
      store.setAccessToken('at', ACCESS_LIFETIME_SECONDS);

      // Exactly what the browser does once the 3-hour Max-Age is up.
      document.cookie = `${ACCESS_COOKIE}=; Max-Age=0; Path=/`;

      const restored = freshStore();
      expect(restored.accessToken()).toBeNull();
      expect(restored.mayHaveSession()).toBe(true);
      expect(restored.isSignedIn()).toBe(true);
    });

    it('carries no credential, only a deadline', () => {
      freshStore().setAccessToken('at', ACCESS_LIFETIME_SECONDS);

      const hint = localStorage.getItem(HINT_KEY);
      expect(hint).not.toBeNull();
      expect(hint).not.toContain('at');
      expect(Number(hint)).toBeGreaterThan(Date.now());
    });

    it('is cleared by clear(), so the next boot does not attempt a doomed refresh', () => {
      const store = freshStore();
      store.setAccessToken('at', ACCESS_LIFETIME_SECONDS);

      store.clear();

      expect(store.isSignedIn()).toBe(false);
      expect(localStorage.getItem(HINT_KEY)).toBeNull();
      expect(freshStore().mayHaveSession()).toBe(false);
    });

    it('survives forgetAccessToken, which only drops the short-lived half', () => {
      const store = freshStore();
      store.setAccessToken('at', ACCESS_LIFETIME_SECONDS);

      store.forgetAccessToken();

      expect(store.accessToken()).toBeNull();
      expect(store.mayHaveSession()).toBe(true);
    });

    /**
     * The reason the hint is in localStorage and not sessionStorage. Come back the next
     * morning and the access cookie is hours dead, but the refresh cookie has six
     * days left — a hint that died with the tab would strand it and force a password.
     */
    it('outlives the tab, because the refresh cookie does', () => {
      freshStore().setAccessToken('at', ACCESS_LIFETIME_SECONDS);

      // Closing the tab clears sessionStorage and nothing else — deliberately NOT
      // clearBrowserState(), which is a whole-browser reset and would prove nothing here.
      sessionStorage.clear();

      expect(freshStore().mayHaveSession()).toBe(true);
    });

    it('is written with the cookie’s seven-day deadline', () => {
      const now = 1_700_000_000_000;
      freshStore().setAccessToken('at', ACCESS_LIFETIME_SECONDS, now);

      expect(Number(localStorage.getItem(HINT_KEY))).toBe(now + REFRESH_LIFETIME_MS);
      expect(REFRESH_LIFETIME_MS).toBe(7 * 24 * 60 * 60 * 1000);
    });

    // Past seven days the refresh cookie is gone, so a refresh would only earn a 401.
    it('stops claiming a session once the seven days are up', () => {
      const store = freshStore();
      store.setAccessToken('at', ACCESS_LIFETIME_SECONDS);

      // A week later: the access cookie expired on day one, and the hint has
      // just run out too.
      document.cookie = `${ACCESS_COOKIE}=; Max-Age=0; Path=/`;
      localStorage.setItem(HINT_KEY, String(Date.now() - 1));

      const nextMorning = freshStore();
      expect(nextMorning.mayHaveSession()).toBe(false);
      expect(nextMorning.isSignedIn()).toBe(false);
      // And the dead entry is swept rather than left to linger.
      expect(localStorage.getItem(HINT_KEY)).toBeNull();
    });

    // Every refresh re-issues the cookie, so an active user's deadline keeps sliding.
    it('slides forward on each refresh instead of counting down from first sign-in', () => {
      const store = freshStore();
      store.setAccessToken('at', ACCESS_LIFETIME_SECONDS, 1_000);
      const afterLogin = Number(localStorage.getItem(HINT_KEY));

      store.setAccessToken('at-2', ACCESS_LIFETIME_SECONDS, 1_000 + 1_700_000);
      const afterRefresh = Number(localStorage.getItem(HINT_KEY));

      expect(afterRefresh).toBeGreaterThan(afterLogin);
    });
  });

  describe('isExpiring', () => {
    it('is false well before the deadline', () => {
      const store = freshStore();
      store.setAccessToken('at', ACCESS_LIFETIME_SECONDS, 0);

      expect(store.isExpiring(0)).toBe(false);
    });

    it('is true within the last minute and after expiry', () => {
      const store = freshStore();
      store.setAccessToken('at', ACCESS_LIFETIME_SECONDS, 0);

      const deadline = ACCESS_LIFETIME_SECONDS * 1000;
      expect(store.isExpiring(deadline - 30_000)).toBe(true);
      expect(store.isExpiring(deadline + 1)).toBe(true);
    });

    it('is false when there is no token', () => {
      expect(freshStore().isExpiring()).toBe(false);
    });
  });
});
