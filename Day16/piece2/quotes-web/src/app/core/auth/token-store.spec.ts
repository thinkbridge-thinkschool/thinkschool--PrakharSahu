import { TestBed } from '@angular/core/testing';

import { TokenStore } from './token-store';

const HINT_KEY = 'quotes-web.has-session';

describe('TokenStore', () => {
  function freshStore(): TokenStore {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({});
    return TestBed.inject(TokenStore);
  }

  beforeEach(() => {
    sessionStorage.clear();
  });

  it('starts signed out', () => {
    const store = freshStore();

    expect(store.isSignedIn()).toBe(false);
    expect(store.accessToken()).toBeNull();
    expect(store.mayHaveSession()).toBe(false);
  });

  it('holds the access token and its deadline', () => {
    const store = freshStore();
    store.setAccessToken('at', 3600, 1_000);

    expect(store.accessToken()).toBe('at');
    expect(store.expiresAt()).toBe(1_000 + 3_600_000);
  });

  /**
   * The security property this design exists for. If the access token were ever written to
   * storage, this test would fail — and so would the reason for using an HttpOnly cookie.
   */
  it('NEVER persists the access token anywhere', () => {
    const store = freshStore();
    store.setAccessToken('super-secret-access-token', 3600);

    const everything = [
      ...Object.keys(sessionStorage).map((k) => sessionStorage.getItem(k) ?? ''),
      ...Object.keys(localStorage).map((k) => localStorage.getItem(k) ?? ''),
      document.cookie,
    ].join('|');

    expect(everything).not.toContain('super-secret-access-token');
    expect(store.accessToken()).toBe('super-secret-access-token');
  });

  it('exposes no refresh token at all — it lives in an HttpOnly cookie', () => {
    const store = freshStore() as unknown as Record<string, unknown>;

    expect(store['refreshToken']).toBeUndefined();
  });

  describe('the session hint', () => {
    it('is set on sign-in and survives a reload', () => {
      freshStore().setAccessToken('at', 3600);

      // A brand-new store, as if the page had been refreshed.
      const restored = freshStore();

      // The access token is gone — it was only ever a variable...
      expect(restored.accessToken()).toBeNull();
      // ...but the app knows a refresh cookie probably exists, so it can recover silently.
      expect(restored.mayHaveSession()).toBe(true);
      expect(restored.isSignedIn()).toBe(true);
    });

    it('carries no credential, only a flag', () => {
      freshStore().setAccessToken('at', 3600);

      expect(sessionStorage.getItem(HINT_KEY)).toBe('1');
    });

    it('is cleared by clear(), so the next boot does not attempt a doomed refresh', () => {
      const store = freshStore();
      store.setAccessToken('at', 3600);

      store.clear();

      expect(store.isSignedIn()).toBe(false);
      expect(sessionStorage.getItem(HINT_KEY)).toBeNull();
      expect(freshStore().mayHaveSession()).toBe(false);
    });

    it('survives forgetAccessToken, which only drops the short-lived half', () => {
      const store = freshStore();
      store.setAccessToken('at', 3600);

      store.forgetAccessToken();

      expect(store.accessToken()).toBeNull();
      expect(store.mayHaveSession()).toBe(true);
    });
  });

  describe('isExpiring', () => {
    it('is false well before the deadline', () => {
      const store = freshStore();
      store.setAccessToken('at', 3600, 0);

      expect(store.isExpiring(0)).toBe(false);
    });

    it('is true within the last minute and after expiry', () => {
      const store = freshStore();
      store.setAccessToken('at', 3600, 0);

      expect(store.isExpiring(3_600_000 - 30_000)).toBe(true);
      expect(store.isExpiring(3_600_000 + 1)).toBe(true);
    });

    it('is false when there is no token', () => {
      expect(freshStore().isExpiring()).toBe(false);
    });
  });
});
