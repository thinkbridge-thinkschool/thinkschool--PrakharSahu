import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef, WritableSignal, runInInjectionContext, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { provideQuotesApiBaseUrl } from '../../../core/config/quotes-api.config';
import { httpStatusOf } from '../../../core/http/http-failure';
import { Quote } from '../domain/quote';
import { QuoteDetailApiClient } from './quote-detail-api.client';
import { QuoteDetailFeed } from './quote-detail-feed';

/** Field-for-field the shape `GET /api/quotes/{id}` returns. */
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

describe('QuoteDetailApiClient', () => {
  let httpTesting: HttpTestingController;
  let selectedId: WritableSignal<number | null>;
  let feed: QuoteDetailFeed;

  function settled(): Promise<void> {
    return TestBed.inject(ApplicationRef).whenStable();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideQuotesApiBaseUrl('/api')],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    selectedId = signal<number | null>(null);
    const client = TestBed.inject(QuoteDetailApiClient);
    feed = runInInjectionContext(TestBed.inject(ApplicationRef).injector, () =>
      client.watch(selectedId),
    );
  });

  afterEach(() => httpTesting.verify());

  it('issues no request while nothing is selected', () => {
    TestBed.tick();

    httpTesting.expectNone(() => true);
    expect(feed.quote()).toBeUndefined();
    expect(feed.failure()).toBeUndefined();
  });

  it('requests GET /api/quotes/{id} for the selected id', async () => {
    selectedId.set(3);
    TestBed.tick();

    const request = httpTesting.expectOne('/api/quotes/3');
    expect(request.request.method).toBe('GET');
    request.flush(wireQuote(3));
    await settled();

    expect(feed.quote()?.id).toBe(3);
    expect(feed.isLoading()).toBe(false);
  });

  it('reports a 404 as a failure carrying status 404, without throwing from quote()', async () => {
    selectedId.set(9999);
    TestBed.tick();
    httpTesting
      .expectOne('/api/quotes/9999')
      .flush('', { status: 404, statusText: 'Not Found' });
    await settled();

    expect(() => feed.quote()).not.toThrow();
    expect(feed.quote()).toBeUndefined();
    expect(httpStatusOf(feed.failure())).toBe(404);
  });

  it('stops requesting and clears the quote when the selection is cleared', async () => {
    selectedId.set(3);
    TestBed.tick();
    httpTesting.expectOne('/api/quotes/3').flush(wireQuote(3));
    await settled();
    expect(feed.quote()?.id).toBe(3);

    selectedId.set(null);
    TestBed.tick();
    await settled();

    httpTesting.expectNone(() => true);
    expect(feed.quote()).toBeUndefined();
  });

  // THE RACE. Select 3, then select 4 before 3 has answered, then let the slow response
  // for 3 arrive last. The pane must show 4.
  it('never lets a stale response overwrite a newer selection', async () => {
    selectedId.set(3);
    TestBed.tick();
    const slowRequestForThree = httpTesting.expectOne('/api/quotes/3');

    selectedId.set(4);
    TestBed.tick();
    const requestForFour = httpTesting.expectOne('/api/quotes/4');

    // Changing the reactive URL aborts the in-flight request, so the stale answer can
    // never land at all - it is not merely ignored after the fact.
    expect(slowRequestForThree.cancelled).toBe(true);

    requestForFour.flush(wireQuote(4));
    await settled();

    expect(feed.quote()?.id).toBe(4);
    expect(feed.failure()).toBeUndefined();
  });

  it('recovers on reload after a failure', async () => {
    selectedId.set(3);
    TestBed.tick();
    httpTesting.expectOne('/api/quotes/3').flush('', { status: 500, statusText: 'Server Error' });
    await settled();
    expect(httpStatusOf(feed.failure())).toBe(500);

    feed.reload();
    TestBed.tick();
    httpTesting.expectOne('/api/quotes/3').flush(wireQuote(3));
    await settled();

    expect(feed.quote()?.id).toBe(3);
    expect(feed.failure()).toBeUndefined();
  });
});
