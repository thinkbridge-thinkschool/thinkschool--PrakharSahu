import { HttpClient, HttpErrorResponse, HttpHeaders, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import {
  backoffDelayMs,
  isRetryableFailure,
  provideRetryPolicy,
  retryAfterMs,
  retryIdempotentInterceptor,
} from './retry-idempotent.interceptor';

const POLICY = { maxRetries: 2, baseDelayMs: 0, maxDelayMs: 0 };

async function settle(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe('backoffDelayMs', () => {
  const policy = { maxRetries: 3, baseDelayMs: 300, maxDelayMs: 4000 };

  it('doubles each attempt before jitter', () => {
    const noJitter = () => 1;
    expect(backoffDelayMs(1, policy, noJitter)).toBe(300);
    expect(backoffDelayMs(2, policy, noJitter)).toBe(600);
    expect(backoffDelayMs(3, policy, noJitter)).toBe(1200);
  });

  it('caps the exponential growth', () => {
    expect(backoffDelayMs(10, policy, () => 1)).toBe(4000);
  });

  it('applies full jitter, so simultaneous clients do not retry in lockstep', () => {
    expect(backoffDelayMs(3, policy, () => 0)).toBe(0);
    expect(backoffDelayMs(3, policy, () => 0.5)).toBe(600);
    expect(backoffDelayMs(3, policy, () => 1)).toBe(1200);
  });
});

describe('retryAfterMs', () => {
  const withHeader = (value: string) =>
    new HttpErrorResponse({ status: 429, headers: new HttpHeaders({ 'Retry-After': value }) });

  it('reads a delay given in seconds', () => {
    expect(retryAfterMs(withHeader('2'))).toBe(2000);
  });

  it('reads a delay given as an HTTP date', () => {
    const now = Date.parse('2026-08-25T10:00:00Z');
    expect(retryAfterMs(withHeader('Tue, 25 Aug 2026 10:00:05 GMT'), now)).toBe(5000);
  });

  it('treats a date in the past as retry immediately', () => {
    const now = Date.parse('2026-08-25T10:00:10Z');
    expect(retryAfterMs(withHeader('Tue, 25 Aug 2026 10:00:00 GMT'), now)).toBe(0);
  });

  it('ignores nonsense and absent headers', () => {
    expect(retryAfterMs(withHeader('soon'))).toBeNull();
    expect(retryAfterMs(new HttpErrorResponse({ status: 429 }))).toBeNull();
  });
});

describe('isRetryableFailure', () => {
  const at = (status: number) => new HttpErrorResponse({ status });

  it('retries transport failures and the transient statuses', () => {
    for (const status of [0, 408, 429, 500, 502, 503, 504]) {
      expect(isRetryableFailure(at(status)), `status ${status}`).toBe(true);
    }
  });

  it('never retries a request the server understood and refused', () => {
    for (const status of [400, 401, 403, 404, 409, 422]) {
      expect(isRetryableFailure(at(status)), `status ${status}`).toBe(false);
    }
  });

  it('ignores anything that is not an HttpErrorResponse', () => {
    expect(isRetryableFailure(new Error('boom'))).toBe(false);
  });
});

describe('retryIdempotentInterceptor', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([retryIdempotentInterceptor])),
        provideHttpClientTesting(),
        provideRetryPolicy(POLICY),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  it('retries a GET and succeeds on the third attempt', async () => {
    const pending = firstValueFrom(http.get('/api/quotes'));

    httpTesting.expectOne('/api/quotes').flush('', { status: 500, statusText: 'Server Error' });
    await settle();
    httpTesting.expectOne('/api/quotes').flush('', { status: 500, statusText: 'Server Error' });
    await settle();
    httpTesting.expectOne('/api/quotes').flush([{ id: 1 }]);

    await expect(pending).resolves.toEqual([{ id: 1 }]);
  });

  it('gives up after maxRetries and reports the last failure', async () => {
    const pending = firstValueFrom(http.get('/api/quotes')).catch((e: unknown) => e);

    for (let i = 0; i < 3; i += 1) {
      httpTesting.expectOne('/api/quotes').flush('', { status: 503, statusText: 'Unavailable' });
      await settle();
    }

    const failure = (await pending) as HttpErrorResponse;
    expect(failure.status).toBe(503);
  });

  // The important one: replaying a POST would create a second quote, and POST /api/quotes
  // has no idempotency key to deduplicate with.
  it('never retries a POST, even on a retryable status', async () => {
    const pending = firstValueFrom(http.post('/api/quotes', {})).catch((e: unknown) => e);

    httpTesting.expectOne('/api/quotes').flush('', { status: 503, statusText: 'Unavailable' });
    await settle();

    httpTesting.expectNone('/api/quotes');
    expect(((await pending) as HttpErrorResponse).status).toBe(503);
  });

  it('never retries PUT or DELETE either', async () => {
    const put = firstValueFrom(http.put('/api/quotes/1/author', {})).catch((e: unknown) => e);
    httpTesting.expectOne('/api/quotes/1/author').flush('', { status: 500, statusText: 'x' });
    await settle();
    httpTesting.expectNone('/api/quotes/1/author');
    await put;

    const del = firstValueFrom(http.delete('/api/quotes/1')).catch((e: unknown) => e);
    httpTesting.expectOne('/api/quotes/1').flush('', { status: 500, statusText: 'x' });
    await settle();
    httpTesting.expectNone('/api/quotes/1');
    await del;
  });

  it('does not retry a 404 — the real API answers unknown ids that way', async () => {
    const pending = firstValueFrom(http.get('/api/quotes/9999')).catch((e: unknown) => e);

    httpTesting.expectOne('/api/quotes/9999').flush('', { status: 404, statusText: 'Not Found' });
    await settle();

    httpTesting.expectNone('/api/quotes/9999');
    expect(((await pending) as HttpErrorResponse).status).toBe(404);
  });

  it('does not retry a 401 or 403 — replaying will be refused identically', async () => {
    for (const status of [401, 403]) {
      const pending = firstValueFrom(http.get('/api/quotes')).catch((e: unknown) => e);
      httpTesting.expectOne('/api/quotes').flush('', { status, statusText: 'x' });
      await settle();
      httpTesting.expectNone('/api/quotes');
      expect(((await pending) as HttpErrorResponse).status).toBe(status);
    }
  });

  it('retries a transport failure, where status is 0', async () => {
    const pending = firstValueFrom(http.get('/api/quotes'));

    httpTesting
      .expectOne('/api/quotes')
      .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
    await settle();
    httpTesting.expectOne('/api/quotes').flush([]);

    await expect(pending).resolves.toEqual([]);
  });
});
