import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { InjectionToken, Provider, inject } from '@angular/core';
import { timer } from 'rxjs';
import { retry } from 'rxjs/operators';

/**
 * Methods that are safe to replay.
 *
 * Idempotency is about the effect on the server, not about whether a retry is convenient.
 * A replayed `POST /api/quotes` creates a second quote, and the Week-1 API has no
 * idempotency key to deduplicate with, so POST is never retried here.
 */
const IDEMPOTENT_METHODS: ReadonlySet<string> = new Set(['GET', 'HEAD', 'OPTIONS']);

/**
 * Statuses worth replaying. Deliberately narrow.
 *
 * 4xx are the server saying "your request is wrong" — replaying it unchanged just asks the
 * same wrong question again. The two exceptions are 408 (the server gave up waiting) and
 * 429 (it asked us to slow down, not to stop).
 */
const RETRYABLE_STATUSES: ReadonlySet<number> = new Set([408, 429, 500, 502, 503, 504]);

export interface RetryPolicy {
  /** Retries AFTER the first attempt. 2 means up to three requests in total. */
  readonly maxRetries: number;
  readonly baseDelayMs: number;
  readonly maxDelayMs: number;
}

export const RETRY_POLICY = new InjectionToken<RetryPolicy>('RETRY_POLICY', {
  providedIn: 'root',
  factory: () => ({ maxRetries: 2, baseDelayMs: 300, maxDelayMs: 4000 }),
});

export function provideRetryPolicy(policy: RetryPolicy): Provider {
  return { provide: RETRY_POLICY, useValue: policy };
}

/**
 * Exponential backoff with full jitter.
 *
 * Jitter matters more than the exponent: without it, every client that failed together
 * retries together and the server gets the same spike a second later. `random` is a
 * parameter so the schedule can be asserted in a test instead of hoped for.
 */
export function backoffDelayMs(
  attempt: number,
  policy: RetryPolicy,
  random: () => number = Math.random,
): number {
  const exponential = policy.baseDelayMs * 2 ** (attempt - 1);
  return Math.round(Math.min(policy.maxDelayMs, exponential) * random());
}

/** Honours `Retry-After`, in seconds or as an HTTP date. Returns null when absent or absurd. */
export function retryAfterMs(response: HttpErrorResponse, now: number = Date.now()): number | null {
  const header = response.headers?.get('Retry-After');
  if (!header) {
    return null;
  }

  const seconds = Number(header);
  if (Number.isFinite(seconds)) {
    return seconds >= 0 ? seconds * 1000 : null;
  }

  const dateMs = Date.parse(header);
  if (Number.isNaN(dateMs)) {
    return null;
  }
  const delta = dateMs - now;
  return delta > 0 ? delta : 0;
}

export function isRetryableFailure(error: unknown): error is HttpErrorResponse {
  if (!(error instanceof HttpErrorResponse)) {
    return false;
  }
  // Status 0 is a transport failure — the request never got an answer, so replaying it is
  // exactly the right move.
  return error.status === 0 || RETRYABLE_STATUSES.has(error.status);
}

/**
 * Retries idempotent requests with backoff.
 *
 * MUST be registered LAST in `withInterceptors([...])`. Angular builds the chain in array
 * order, so the last entry sits closest to the backend: it is the only position where this
 * interceptor both re-issues the real request and still sees a raw `HttpErrorResponse`.
 * Put it before the error mapper and it receives an already-mapped `ApiError`, every status
 * check silently fails, and nothing is ever retried.
 */
export const retryIdempotentInterceptor: HttpInterceptorFn = (request, next) => {
  if (!IDEMPOTENT_METHODS.has(request.method.toUpperCase())) {
    return next(request);
  }

  const policy = inject(RETRY_POLICY);

  return next(request).pipe(
    retry({
      count: policy.maxRetries,
      delay: (error: unknown, attempt: number) => {
        if (!isRetryableFailure(error)) {
          // Rethrowing from `delay` ends the retry sequence and propagates the original
          // failure untouched — a 404 must surface immediately, not three seconds later.
          throw error;
        }
        const serverAsked = retryAfterMs(error);
        return timer(serverAsked ?? backoffDelayMs(attempt, policy));
      },
    }),
  );
};
