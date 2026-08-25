import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef, Signal, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { provideQuotesApiBaseUrl } from '../../../core/config/quotes-api.config';
import { QuoteDetailApiClient } from '../data-access/quote-detail-api.client';
import { QuoteDetailFeed } from '../data-access/quote-detail-feed';
import { Quote } from '../domain/quote';
import { QuoteDetailStore } from './quote-detail-store';

function wireQuote(id: number): Quote {
  return {
    id,
    text: `Quote number ${id}`,
    author: 'Ada Lovelace',
    createdAt: '2026-08-10T10:00:00+00:00',
    isDeleted: false,
    userId: 1,
  };
}

/** Drives the store's states directly, with no HTTP stack in sight. */
class FakeQuoteDetailFeed implements QuoteDetailFeed {
  readonly loaded = signal<Quote | undefined>(undefined);
  readonly loading = signal(false);
  readonly error = signal<Error | undefined>(undefined);
  reloadCount = 0;

  readonly quote: Signal<Quote | undefined> = this.loaded.asReadonly();
  readonly isLoading = this.loading.asReadonly();
  readonly failure = this.error.asReadonly();

  reload(): void {
    this.reloadCount += 1;
  }
}

describe('QuoteDetailStore status machine', () => {
  let feed: FakeQuoteDetailFeed;
  let store: QuoteDetailStore;

  beforeEach(() => {
    feed = new FakeQuoteDetailFeed();
    TestBed.configureTestingModule({
      providers: [{ provide: QuoteDetailApiClient, useValue: { watch: () => feed } }],
    });
    store = TestBed.inject(QuoteDetailStore);
  });

  it('starts idle with nothing selected', () => {
    expect(store.status()).toBe('idle');
    expect(store.selectedQuoteId()).toBeNull();
    expect(store.quote()).toBeUndefined();
  });

  it('is loading once an id is selected and the request is in flight', () => {
    store.select(3);
    feed.loading.set(true);

    expect(store.status()).toBe('loading');
  });

  it('is ready once the quote arrives', () => {
    store.select(3);
    feed.loaded.set(wireQuote(3));

    expect(store.status()).toBe('ready');
    expect(store.quote()?.id).toBe(3);
  });

  it('separates a 404 from a fault and gives it no error message', () => {
    store.select(9999);
    feed.error.set(new HttpErrorResponse({ status: 404, url: '/api/quotes/9999' }));

    expect(store.status()).toBe('not-found');
    expect(store.isNotFound()).toBe(true);
    expect(store.failureMessage()).toBeNull();
  });

  it('treats a 500 as a fault with an actionable message', () => {
    store.select(3);
    feed.error.set(new HttpErrorResponse({ status: 500, url: '/api/quotes/3' }));

    expect(store.status()).toBe('failed');
    expect(store.isNotFound()).toBe(false);
    expect(store.failureMessage()).toContain('500');
  });

  it('returns to idle when closed, whatever state it was in', () => {
    store.select(9999);
    feed.error.set(new HttpErrorResponse({ status: 404, url: '/api/quotes/9999' }));
    expect(store.status()).toBe('not-found');

    store.close();

    expect(store.status()).toBe('idle');
    expect(store.selectedQuoteId()).toBeNull();
  });

  it('delegates reload to the feed', () => {
    store.select(3);
    store.reload();

    expect(feed.reloadCount).toBe(1);
  });
});

describe('QuoteDetailStore over the real transport', () => {
  let httpTesting: HttpTestingController;
  let store: QuoteDetailStore;

  function settled(): Promise<void> {
    return TestBed.inject(ApplicationRef).whenStable();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideQuotesApiBaseUrl('/api')],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    store = TestBed.inject(QuoteDetailStore);
  });

  afterEach(() => httpTesting.verify());

  it('walks idle -> loading -> ready against GET /api/quotes/{id}', async () => {
    expect(store.status()).toBe('idle');

    store.select(3);
    TestBed.tick();
    expect(store.status()).toBe('loading');

    httpTesting.expectOne('/api/quotes/3').flush(wireQuote(3));
    await settled();

    expect(store.status()).toBe('ready');
    expect(store.quote()?.id).toBe(3);
  });

  it('does not keep showing the previous quote while the next one loads', async () => {
    store.select(3);
    TestBed.tick();
    httpTesting.expectOne('/api/quotes/3').flush(wireQuote(3));
    await settled();
    expect(store.quote()?.id).toBe(3);

    store.select(4);
    TestBed.tick();

    expect(store.status()).toBe('loading');
    expect(store.quote()).toBeUndefined();

    httpTesting.expectOne('/api/quotes/4').flush(wireQuote(4));
    await settled();
    expect(store.quote()?.id).toBe(4);
  });

  it('shows not-found for an id the API 404s, without a fault message', async () => {
    store.select(9999);
    TestBed.tick();
    httpTesting.expectOne('/api/quotes/9999').flush('', { status: 404, statusText: 'Not Found' });
    await settled();

    expect(store.status()).toBe('not-found');
    expect(store.failureMessage()).toBeNull();
  });

  // A 200 whose body is not a quote must not present as a spinner that never resolves.
  it('reports a bodiless 200 as a fault rather than loading forever', async () => {
    store.select(3);
    TestBed.tick();
    httpTesting.expectOne('/api/quotes/3').flush(null, { status: 200, statusText: 'OK' });
    await settled();

    expect(store.status()).not.toBe('loading');
    expect(store.status()).toBe('failed');
    expect(store.failureMessage()).toBeTruthy();
  });

  // The race, through the store this time: two fast clicks, slow first answer.
  it('shows the last-clicked quote when responses interleave', async () => {
    store.select(3);
    TestBed.tick();
    const stale = httpTesting.expectOne('/api/quotes/3');

    store.select(4);
    TestBed.tick();
    const current = httpTesting.expectOne('/api/quotes/4');

    expect(stale.cancelled).toBe(true);
    current.flush(wireQuote(4));
    await settled();

    expect(store.selectedQuoteId()).toBe(4);
    expect(store.quote()?.id).toBe(4);
    expect(store.status()).toBe('ready');
  });
});
