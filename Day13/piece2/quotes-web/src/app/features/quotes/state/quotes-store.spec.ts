import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { QuotesApiClient } from '../data-access/quotes-api.client';
import { QuotesFeed } from '../data-access/quotes-feed';
import { Quote } from '../domain/quote';
import { QuotesStore } from './quotes-store';

const PREFERENCES_KEY = 'quotes-web.list-preferences';

function quoteFixture(overrides: Partial<Quote> & Pick<Quote, 'id'>): Quote {
  return {
    text: 'Prefer CTEs to correlated subqueries.',
    author: 'Ada Lovelace',
    createdAt: '2026-08-10T10:00:00+00:00',
    isDeleted: false,
    userId: 1,
    ...overrides,
  };
}

/** Stands in for `QuotesApiClient` so the store can be driven without an HTTP stack. */
class FakeQuotesFeed implements QuotesFeed {
  readonly source = signal<readonly Quote[]>([]);
  readonly loading = signal(false);
  readonly error = signal<Error | undefined>(undefined);
  refreshCount = 0;

  readonly quotes = this.source.asReadonly();
  readonly isLoading = this.loading.asReadonly();
  readonly failure = this.error.asReadonly();

  refresh(): void {
    this.refreshCount += 1;
  }
}

function createStore(feed: FakeQuotesFeed): QuotesStore {
  TestBed.configureTestingModule({
    providers: [{ provide: QuotesApiClient, useValue: feed }],
  });
  return TestBed.inject(QuotesStore);
}

describe('QuotesStore', () => {
  let feed: FakeQuotesFeed;

  const ada = quoteFixture({ id: 1, author: 'Ada Lovelace', createdAt: '2026-08-10T10:00:00+00:00' });
  const grace = quoteFixture({ id: 2, author: 'Grace Hopper', createdAt: '2026-08-16T14:00:00+00:00' });

  beforeEach(() => {
    sessionStorage.clear();
    feed = new FakeQuotesFeed();
  });

  describe('visibleQuotes', () => {
    it('recomputes when the feed delivers quotes', () => {
      const store = createStore(feed);
      expect(store.visibleQuotes()).toEqual([]);

      feed.source.set([ada, grace]);

      expect(store.visibleQuotes().map((q) => q.id)).toEqual([2, 1]);
    });

    it('recomputes when search changes', () => {
      const store = createStore(feed);
      feed.source.set([ada, grace]);

      store.search.set('grace');

      expect(store.visibleQuotes().map((q) => q.id)).toEqual([2]);
      expect(store.visibleCount()).toBe(1);
      expect(store.totalCount()).toBe(2);
    });

    it('recomputes when sortOrder changes', () => {
      const store = createStore(feed);
      feed.source.set([ada, grace]);
      expect(store.visibleQuotes().map((q) => q.id)).toEqual([2, 1]);

      store.sortOrder.set('oldest');

      expect(store.visibleQuotes().map((q) => q.id)).toEqual([1, 2]);
    });

    it('feeds authorTallies from the visible list, not the whole feed', () => {
      const store = createStore(feed);
      feed.source.set([ada, grace]);

      store.search.set('ada');

      expect(store.authorTallies()).toEqual([{ author: 'Ada Lovelace', count: 1 }]);
    });
  });

  describe('status', () => {
    it('is loading only while the first request is in flight', () => {
      const store = createStore(feed);
      feed.loading.set(true);

      expect(store.status()).toBe('loading');
      expect(store.isRefreshing()).toBe(false);
    });

    it('is empty when the API returns no quotes', () => {
      const store = createStore(feed);
      expect(store.status()).toBe('empty');
    });

    it('is no-matches when the filter excludes everything', () => {
      const store = createStore(feed);
      feed.source.set([ada, grace]);

      store.search.set('kubernetes');

      expect(store.status()).toBe('no-matches');
    });

    it('is ready once quotes survive the filter', () => {
      const store = createStore(feed);
      feed.source.set([ada]);

      expect(store.status()).toBe('ready');
    });

    it('is failed whenever the feed reports an error, even with cached quotes', () => {
      const store = createStore(feed);
      feed.source.set([ada]);
      feed.error.set(new Error('boom'));

      expect(store.status()).toBe('failed');
    });

    it('reports a reload over existing quotes as refreshing, not loading', () => {
      const store = createStore(feed);
      feed.source.set([ada]);
      feed.loading.set(true);

      expect(store.isRefreshing()).toBe(true);
      expect(store.status()).toBe('ready');
    });
  });

  describe('failureMessage', () => {
    it('is null while healthy', () => {
      const store = createStore(feed);
      expect(store.failureMessage()).toBeNull();
    });

    // Angular leaves error-like throwables alone, so a failed request surfaces the
    // `HttpErrorResponse` itself. The wrapped form is covered too, since `resource` only
    // wraps throwables that are *not* error-like.
    it('turns an unreachable API into an actionable sentence', () => {
      const store = createStore(feed);
      feed.error.set(new HttpErrorResponse({ status: 0, url: '/api/quotes' }));

      expect(store.failureMessage()).toContain('Could not reach the Quotes API');
    });

    it('reports the status code for a server-side failure', () => {
      const store = createStore(feed);
      feed.error.set(new HttpErrorResponse({ status: 500, url: '/api/quotes' }));

      expect(store.failureMessage()).toContain('500');
    });

    it('unwraps a failure that arrived on Error.cause', () => {
      const store = createStore(feed);
      feed.error.set(
        new Error('failed', { cause: new HttpErrorResponse({ status: 404, url: '/api/quotes' }) }),
      );

      expect(store.failureMessage()).toContain('endpoint was not found');
    });

    it('falls back to a generic sentence for a non-HTTP error', () => {
      const store = createStore(feed);
      feed.error.set(new Error('JSON parse failed'));

      expect(store.failureMessage()).toBe('JSON parse failed');
    });
  });

  describe('actions', () => {
    it('clearSearch empties the search signal', () => {
      const store = createStore(feed);
      store.search.set('ada');

      store.clearSearch();

      expect(store.search()).toBe('');
      expect(store.hasActiveSearch()).toBe(false);
    });

    it('refresh delegates to the feed', () => {
      const store = createStore(feed);

      store.refresh();

      expect(feed.refreshCount).toBe(1);
    });
  });

  describe('preference persistence', () => {
    it('writes search and sortOrder to sessionStorage', () => {
      const store = createStore(feed);

      store.search.set('grace');
      store.sortOrder.set('oldest');
      TestBed.tick();

      expect(JSON.parse(sessionStorage.getItem(PREFERENCES_KEY) ?? '{}')).toEqual({
        search: 'grace',
        sortOrder: 'oldest',
      });
    });

    it('restores previously stored preferences', () => {
      sessionStorage.setItem(
        PREFERENCES_KEY,
        JSON.stringify({ search: 'ada', sortOrder: 'oldest' }),
      );

      const store = createStore(feed);

      expect(store.search()).toBe('ada');
      expect(store.sortOrder()).toBe('oldest');
    });

    it('falls back to defaults when the stored value is malformed', () => {
      sessionStorage.setItem(PREFERENCES_KEY, JSON.stringify({ search: 42, sortOrder: 'sideways' }));

      const store = createStore(feed);

      expect(store.search()).toBe('');
      expect(store.sortOrder()).toBe('newest');
    });
  });
});
