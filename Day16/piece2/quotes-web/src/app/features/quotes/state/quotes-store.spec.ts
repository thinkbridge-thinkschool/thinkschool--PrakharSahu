import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { HTTP_INTERCEPTOR_CHAIN } from '../../../app.config';
import { TokenStore } from '../../../core/auth/token-store';
import { provideQuotesApiBaseUrl } from '../../../core/config/quotes-api.config';
import { provideRetryPolicy } from '../../../core/http/retry-idempotent.interceptor';
import { Quote } from '../domain/quote';
import { QuotesStore } from './quotes-store';

/** Ids 1, 2 and 6 belong to the seeded dev user; 3 belongs to somebody else. */
function quote(id: number, userId = 1): Quote {
  return {
    id,
    text: `Quote number ${id}`,
    author: userId === 1 ? 'dev@quotes.local' : 'mentor@thinkbridge.com',
    createdAt: '2026-08-12T09:00:00+00:00',
    isDeleted: false,
    userId,
  };
}

const THREE = [quote(1), quote(2), quote(3, 2)];

describe('QuotesStore', () => {
  let store: QuotesStore;
  let httpTesting: HttpTestingController;
  let tokens: TokenStore;

  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }

  const ids = () => store.quotes().map((q) => q.id);

  beforeEach(() => {
    // TokenStore restores from sessionStorage at construction, so a session written by an
    // earlier test would leak into this one and make 'signed out' cases pass wrongly.
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        // The real chain, so failures arrive as ApiError exactly as they do in the app.
        provideHttpClient(withInterceptors([...HTTP_INTERCEPTOR_CHAIN])),
        provideHttpClientTesting(),
        provideQuotesApiBaseUrl('/api'),
        // Retries off. In production a 5xx GET is retried twice with backoff — that is Day
        // 15's behaviour and Day 15 tests it. Leaving it on here would make every 5xx store
        // test flush three times and assert nothing extra about the store.
        provideRetryPolicy({ maxRetries: 0, baseDelayMs: 0, maxDelayMs: 0 }),
      ],
    });
    store = TestBed.inject(QuotesStore);
    httpTesting = TestBed.inject(HttpTestingController);
    tokens = TestBed.inject(TokenStore);
    tokens.setAccessToken('a-token', 3600);
  });

  afterEach(() => httpTesting.verify({ ignoreCancelled: true }));

  describe('loading', () => {
    it('starts idle without calling the API', () => {
      expect(store.viewStatus()).toBe('idle');
      httpTesting.expectNone(() => true);
    });

    it('reports loading, then ready with the payload', async () => {
      const pending = store.load();
      expect(store.viewStatus()).toBe('loading');

      httpTesting.expectOne('/api/quotes').flush(THREE);
      await pending;

      expect(store.viewStatus()).toBe('ready');
      expect(ids()).toEqual([1, 2, 3]);
    });

    it('distinguishes an empty list from a failure', async () => {
      const pending = store.load();
      httpTesting.expectOne('/api/quotes').flush([]);
      await pending;

      expect(store.viewStatus()).toBe('empty');
      expect(store.error()).toBeNull();
    });

    it('surfaces a load failure as a typed, friendly error', async () => {
      const pending = store.load();
      httpTesting.expectOne('/api/quotes').flush('', { status: 500, statusText: 'Server Error' });
      await pending;

      expect(store.viewStatus()).toBe('failed');
      expect(store.error()?.kind).toBe('server');
      expect(store.error()?.friendlyMessage).toBe(
        'The Quotes API is having trouble. Try again shortly.',
      );
    });

    it('does not blank a populated list while refreshing', async () => {
      const first = store.load();
      httpTesting.expectOne('/api/quotes').flush(THREE);
      await first;

      const refresh = store.load();
      // Still showing the previous rows rather than dropping to a skeleton.
      expect(store.viewStatus()).toBe('ready');
      expect(ids()).toEqual([1, 2, 3]);

      httpTesting.expectOne('/api/quotes').flush([quote(1)]);
      await refresh;
      expect(ids()).toEqual([1]);
    });
  });

  describe('concurrent loads', () => {
    // The classic out-of-order bug: two refreshes in flight, the SLOWER one answers last and
    // wins purely by arriving late.
    it('ignores a stale response that lands after a newer one', async () => {
      const first = store.load();
      const firstRequest = httpTesting.expectOne('/api/quotes');

      const second = store.load();
      const secondRequest = httpTesting.expectOne('/api/quotes');

      // Newer answers first...
      secondRequest.flush([quote(9)]);
      await second;
      expect(ids()).toEqual([9]);

      // ...then the older one arrives with what is now stale data.
      firstRequest.flush(THREE);
      await first;

      expect(ids()).toEqual([9]);
    });

    it('does not let a stale failure clobber a good newer result', async () => {
      const first = store.load();
      const firstRequest = httpTesting.expectOne('/api/quotes');

      const second = store.load();
      httpTesting.expectOne('/api/quotes').flush(THREE);
      await second;

      firstRequest.flush('', { status: 500, statusText: 'Server Error' });
      await first;

      expect(store.viewStatus()).toBe('ready');
      expect(store.error()).toBeNull();
    });
  });

  describe('optimistic delete', () => {
    async function loaded(): Promise<void> {
      const pending = store.load();
      httpTesting.expectOne('/api/quotes').flush(THREE);
      await pending;
    }

    it('hides the row immediately, before the server answers', async () => {
      await loaded();

      const pending = store.remove(2);
      expect(ids()).toEqual([1, 3]);
      expect(store.isRemoving(2)).toBe(true);

      httpTesting.expectOne('/api/quotes/2').flush(null, { status: 204, statusText: 'No Content' });
      await pending;

      expect(ids()).toEqual([1, 3]);
      expect(store.isRemoving(2)).toBe(false);
    });

    it('sends DELETE to the real endpoint', async () => {
      await loaded();
      const pending = store.remove(2);

      const request = httpTesting.expectOne('/api/quotes/2');
      expect(request.request.method).toBe('DELETE');
      request.flush(null, { status: 204, statusText: 'No Content' });
      await pending;
    });

    // 403 is not hypothetical: the seeded data contains quotes owned by another user, and
    // IsOwnerHandler refuses them.
    it('restores the row and explains a 403 from IsOwnerHandler', async () => {
      await loaded();

      const pending = store.remove(3);
      expect(ids()).toEqual([1, 2]);

      httpTesting.expectOne('/api/quotes/3').flush('', { status: 403, statusText: 'Forbidden' });
      await pending;

      expect(ids()).toEqual([1, 2, 3]);
      expect(store.removalFailures()).toEqual([
        { id: 3, message: 'Quote #3 belongs to someone else, so it was not deleted.' },
      ]);
    });

    // A 401 now attempts a token refresh before giving up — refreshOn401Interceptor. Here
    // the refresh token is spent too, so the original 401 surfaces and the session is cleared.
    it('explains a 401 differently from a 403, after the refresh attempt fails', async () => {
      await loaded();
      const pending = store.remove(2);
      httpTesting.expectOne('/api/quotes/2').flush('', { status: 401, statusText: 'Unauthorized' });
      await settle();

      httpTesting
        .expectOne('/api/auth/refresh')
        .flush('', { status: 401, statusText: 'Unauthorized' });
      await pending;

      expect(store.removalFailures()[0]?.message).toContain('session expired');
      expect(tokens.isSignedIn()).toBe(false);
    });

    it('treats a 404 as already gone', async () => {
      await loaded();
      const pending = store.remove(2);
      httpTesting.expectOne('/api/quotes/2').flush('', { status: 404, statusText: 'Not Found' });
      await pending;

      expect(store.removalFailures()[0]?.message).toContain('already been deleted');
    });

    it('ignores a second click while the first delete is in flight', async () => {
      await loaded();

      const first = store.remove(2);
      void store.remove(2);

      httpTesting.expectOne('/api/quotes/2').flush(null, { status: 204, statusText: 'No Content' });
      await first;
      httpTesting.expectNone('/api/quotes/2');
    });

    it('goes empty once the last visible row is removed', async () => {
      const pending = store.load();
      httpTesting.expectOne('/api/quotes').flush([quote(1)]);
      await pending;

      const removal = store.remove(1);
      expect(store.viewStatus()).toBe('empty');

      httpTesting.expectOne('/api/quotes/1').flush(null, { status: 204, statusText: 'No Content' });
      await removal;
      expect(store.viewStatus()).toBe('empty');
    });
  });

  describe('concurrent deletes', () => {
    async function loaded(): Promise<void> {
      const pending = store.load();
      httpTesting.expectOne('/api/quotes').flush(THREE);
      await pending;
    }

    it('tracks two removals independently and restores only the one that failed', async () => {
      await loaded();

      const removeOwned = store.remove(2);
      const removeForeign = store.remove(3);
      expect(ids()).toEqual([1]);
      expect(store.pendingRemovals()).toBe(2);

      httpTesting.expectOne('/api/quotes/3').flush('', { status: 403, statusText: 'Forbidden' });
      await removeForeign;
      httpTesting.expectOne('/api/quotes/2').flush(null, { status: 204, statusText: 'No Content' });
      await removeOwned;

      // 3 comes back, 2 stays gone.
      expect(ids()).toEqual([1, 3]);
      expect(store.removalFailures().map((f) => f.id)).toEqual([3]);
      expect(store.pendingRemovals()).toBe(0);
    });

    // The case that motivates deriving `quotes` instead of mutating an array.
    it('does not resurrect a pending row when a refresh lands mid-delete', async () => {
      await loaded();

      const removal = store.remove(2);
      expect(ids()).toEqual([1, 3]);

      // A refresh answers while the DELETE is still open — the server has not committed the
      // soft-delete yet, so quote 2 is still in the payload.
      const refresh = store.load();
      httpTesting.expectOne('/api/quotes').flush(THREE);
      await refresh;

      expect(ids()).toEqual([1, 3]);
      expect(store.isRemoving(2)).toBe(true);

      httpTesting.expectOne('/api/quotes/2').flush(null, { status: 204, statusText: 'No Content' });
      await removal;
      expect(ids()).toEqual([1, 3]);
    });

    it('clears stale removal complaints when a refresh succeeds', async () => {
      await loaded();

      const failed = store.remove(3);
      httpTesting.expectOne('/api/quotes/3').flush('', { status: 403, statusText: 'Forbidden' });
      await failed;
      expect(store.removalFailures()).toHaveLength(1);

      const refresh = store.load();
      httpTesting.expectOne('/api/quotes').flush(THREE);
      await refresh;

      expect(store.removalFailures()).toEqual([]);
    });
  });

  describe('permissions', () => {
    it('hides the delete affordance while signed out', () => {
      tokens.clear();
      expect(store.canRemove()).toBe(false);
    });

    it('shows it once a token exists', () => {
      expect(store.canRemove()).toBe(true);
    });
  });
});
