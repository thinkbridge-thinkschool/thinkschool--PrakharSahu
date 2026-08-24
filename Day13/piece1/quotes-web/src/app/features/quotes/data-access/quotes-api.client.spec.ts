import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { provideQuotesApiBaseUrl } from '../../../core/config/quotes-api.config';
import { QuotesApiClient } from './quotes-api.client';

/** One response object copied field-for-field from the running Week-1 API. */
const WIRE_RESPONSE = [
  {
    id: 1,
    text: 'Prefer CTEs to correlated subqueries.',
    author: 'Ada Lovelace',
    createdAt: '2026-08-10T10:00:00+00:00',
    isDeleted: false,
    userId: 1,
  },
];

describe('QuotesApiClient', () => {
  let httpTesting: HttpTestingController;
  let client: QuotesApiClient;

  /** `httpResource` resolves off a microtask, so the signals settle a turn after the flush. */
  function settled(): Promise<void> {
    return TestBed.inject(ApplicationRef).whenStable();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideQuotesApiBaseUrl('/api')],
    });
    client = TestBed.inject(QuotesApiClient);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  it('requests GET /api/quotes off the configured base URL', () => {
    TestBed.tick();

    const request = httpTesting.expectOne('/api/quotes');
    expect(request.request.method).toBe('GET');
    request.flush(WIRE_RESPONSE);
  });

  it('starts with an empty list and no failure', () => {
    expect(client.quotes()).toEqual([]);
    expect(client.failure()).toBeUndefined();

    TestBed.tick();
    httpTesting.expectOne('/api/quotes').flush(WIRE_RESPONSE);
  });

  it('exposes the response as a signal', async () => {
    TestBed.tick();
    httpTesting.expectOne('/api/quotes').flush(WIRE_RESPONSE);
    await settled();

    expect(client.quotes()).toEqual(WIRE_RESPONSE);
    expect(client.isLoading()).toBe(false);
    expect(client.failure()).toBeUndefined();
  });

  // Regression: `resource.value()` throws in the error state even with `defaultValue`,
  // so reading `quotes()` after a failure used to take the page down.
  it('surfaces a transport failure without throwing from quotes()', async () => {
    TestBed.tick();
    httpTesting
      .expectOne('/api/quotes')
      .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
    await settled();

    expect(() => client.quotes()).not.toThrow();
    expect(client.quotes()).toEqual([]);
    expect(client.failure()).toBeInstanceOf(HttpErrorResponse);
    expect((client.failure() as unknown as HttpErrorResponse).status).toBe(0);
  });

  it('re-issues the same request on refresh', async () => {
    TestBed.tick();
    httpTesting.expectOne('/api/quotes').flush(WIRE_RESPONSE);
    await settled();

    client.refresh();
    TestBed.tick();

    httpTesting.expectOne('/api/quotes').flush(WIRE_RESPONSE);
    await settled();
    expect(client.quotes()).toHaveLength(1);
  });
});
