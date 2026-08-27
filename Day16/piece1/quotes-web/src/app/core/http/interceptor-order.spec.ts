import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { HTTP_INTERCEPTOR_CHAIN } from '../../app.config';
import { ApiError } from './api-error';
import { authHeaderInterceptor } from './auth-header.interceptor';
import { errorMappingInterceptor } from './error-mapping.interceptor';
import { provideRetryPolicy, retryIdempotentInterceptor } from './retry-idempotent.interceptor';

/** Zero delays so the retry schedule does not slow the suite down. */
const IMMEDIATE = provideRetryPolicy({ maxRetries: 2, baseDelayMs: 0, maxDelayMs: 0 });

async function settle(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe('interceptor order', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;

  function configure(chain: readonly Parameters<typeof withInterceptors>[0][number][]) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([...chain])),
        provideHttpClientTesting(),
        IMMEDIATE,
      ],
    });
    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  }

  afterEach(() => httpTesting.verify());

  describe('the shipped chain', () => {
    beforeEach(() => configure(HTTP_INTERCEPTOR_CHAIN));

    it('puts retry closest to the backend, so a GET is actually retried', async () => {
      const pending = firstValueFrom(http.get('/api/quotes')).catch((e: unknown) => e);

      httpTesting.expectOne('/api/quotes').flush('', { status: 503, statusText: 'Unavailable' });
      await settle();
      httpTesting.expectOne('/api/quotes').flush('', { status: 503, statusText: 'Unavailable' });
      await settle();
      httpTesting.expectOne('/api/quotes').flush([{ ok: true }]);

      // Three attempts in total: the original plus maxRetries.
      await expect(pending).resolves.toEqual([{ ok: true }]);
    });

    it('still maps the failure once retries are exhausted', async () => {
      const pending = firstValueFrom(http.get('/api/quotes')).catch((e: unknown) => e);

      for (let attempt = 0; attempt < 3; attempt += 1) {
        httpTesting.expectOne('/api/quotes').flush('', { status: 503, statusText: 'Unavailable' });
        await settle();
      }

      const failure = await pending;
      expect(failure).toBeInstanceOf(ApiError);
      expect((failure as ApiError).kind).toBe('server');
      expect((failure as ApiError).friendlyMessage).toBe(
        'The Quotes API is having trouble. Try again shortly.',
      );
    });
  });

  /**
   * The trap, pinned as a test rather than as a comment.
   *
   * With retry ahead of the mapper, the mapper sits closer to the backend and converts the
   * 503 into an `ApiError` first. Retry then sees something that is not an
   * `HttpErrorResponse`, declines to retry, and the request is made exactly once — the
   * feature silently does nothing.
   */
  describe('the wrong order, kept as a regression guard', () => {
    beforeEach(() =>
      configure([authHeaderInterceptor, retryIdempotentInterceptor, errorMappingInterceptor]),
    );

    it('silently disables retry when the mapper runs closer to the backend', async () => {
      const pending = firstValueFrom(http.get('/api/quotes')).catch((e: unknown) => e);

      httpTesting.expectOne('/api/quotes').flush('', { status: 503, statusText: 'Unavailable' });
      await settle();

      // No second attempt is ever made.
      httpTesting.expectNone('/api/quotes');
      const failure = await pending;
      expect(failure).toBeInstanceOf(ApiError);
    });
  });
});
