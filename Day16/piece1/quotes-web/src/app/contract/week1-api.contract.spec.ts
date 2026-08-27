import { HttpClient, HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { toApiError } from '../core/http/api-error';
import { parseProblemDetails, parseValidationErrors } from '../core/http/problem-details';
import { isQuoteArray } from '../features/quotes/domain/quote';
import {
  RECORDED_ANY_PROBLEM_DETAILS,
  RECORDED_ERROR_RESPONSES,
  RECORDED_PAGING_IS_IGNORED,
  RECORDED_QUOTES_200,
  RECORDED_QUOTE_FIELDS,
} from './week1-api.recorded';

/**
 * Characterization of the real Week-1 API.
 *
 * These tests describe what the server DOES, not what it should do. Several of them pin
 * behaviour that is arguably wrong — ignored paging parameters, empty 4xx bodies, a
 * `text/plain` stack trace. That is the point: if any of it changes, a test here goes red
 * and tells us which assumption the client was resting on.
 *
 * The payloads come from `week1-api.recorded.ts`, captured with curl against the running
 * API on 2026-08-25.
 */
describe('Week-1 API contract, as recorded', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  describe('GET /api/quotes', () => {
    it('answers 200 with a BARE ARRAY, not a paged envelope', async () => {
      const pending = firstValueFrom(http.get<unknown>('/api/quotes'));
      httpTesting.expectOne('/api/quotes').flush(RECORDED_QUOTES_200);

      const body = await pending;
      expect(Array.isArray(body)).toBe(true);
      expect(body).not.toHaveProperty('items');
      expect(body).not.toHaveProperty('total');
      expect(body).not.toHaveProperty('page');
    });

    it('returns six fields per quote — id, text, author, createdAt, isDeleted, userId', async () => {
      const pending = firstValueFrom(http.get<unknown>('/api/quotes'));
      httpTesting.expectOne('/api/quotes').flush(RECORDED_QUOTES_200);

      const body = (await pending) as Record<string, unknown>[];
      expect(Object.keys(body[0]).sort()).toEqual([...RECORDED_QUOTE_FIELDS].sort());
      // The brief described {id, author, text}. The wire carries three more.
      expect(RECORDED_QUOTE_FIELDS).toContain('isDeleted');
      expect(RECORDED_QUOTE_FIELDS).toContain('userId');
      expect(RECORDED_QUOTE_FIELDS).toContain('createdAt');
    });

    it('sends createdAt with an explicit offset, not a Z suffix', () => {
      for (const quote of RECORDED_QUOTES_200) {
        expect(quote.createdAt).toMatch(/[+-]\d{2}:\d{2}$/);
        expect(quote.createdAt.endsWith('Z')).toBe(false);
      }
    });

    it('satisfies the runtime guard the client uses', () => {
      expect(isQuoteArray(RECORDED_QUOTES_200)).toBe(true);
    });

    // Pins the finding, so that adding real paging server-side breaks this test on purpose.
    it('IGNORES ?page= and ?size= — there is no paging on this endpoint', () => {
      expect(RECORDED_PAGING_IS_IGNORED.rowsReturned).toBe(
        RECORDED_PAGING_IS_IGNORED.rowsReturnedWithoutParams,
      );
      expect(RECORDED_PAGING_IS_IGNORED.rowsReturned).toBeGreaterThan(
        RECORDED_PAGING_IS_IGNORED.requestedSize,
      );
    });
  });

  describe('4xx responses', () => {
    it('produces no ProblemDetails anywhere — AddProblemDetails() is never called', () => {
      expect(RECORDED_ANY_PROBLEM_DETAILS).toBe(false);
      for (const recorded of Object.values(RECORDED_ERROR_RESPONSES)) {
        expect(recorded.contentType ?? '').not.toContain('application/problem+json');
      }
    });

    it('returns 404 with a completely empty body for an unknown id', async () => {
      const pending = firstValueFrom(http.get('/api/quotes/9999')).catch((e: unknown) => e);
      httpTesting.expectOne('/api/quotes/9999').flush('', { status: 404, statusText: 'Not Found' });

      const failure = (await pending) as HttpErrorResponse;
      expect(failure.status).toBe(404);
      expect(parseProblemDetails(failure)).toBeNull();
      expect(parseValidationErrors(failure).size).toBe(0);
    });

    it('answers a non-integer id with 404 from the route constraint, not 400', () => {
      expect(RECORDED_ERROR_RESPONSES.routeConstraintMiss.status).toBe(404);
    });

    it('returns 401 with an empty body and a WWW-Authenticate header', () => {
      expect(RECORDED_ERROR_RESPONSES.unauthorized.status).toBe(401);
      expect(RECORDED_ERROR_RESPONSES.unauthorized.body).toBe('');
      expect(RECORDED_ERROR_RESPONSES.unauthorized.wwwAuthenticate).toBe('Bearer');
    });

    it('returns 403 with an empty body when the token lacks the quotes.write scope', () => {
      expect(RECORDED_ERROR_RESPONSES.forbidden.status).toBe(403);
      expect(RECORDED_ERROR_RESPONSES.forbidden.body).toBe('');
    });

    it('returns a text/plain .NET exception for malformed JSON', () => {
      const recorded = RECORDED_ERROR_RESPONSES.malformedJson;
      expect(recorded.status).toBe(400);
      expect(recorded.contentType).toContain('text/plain');
      expect(recorded.bodyStartsWith).toContain('BadHttpRequestException');
    });
  });

  describe('what the client must therefore do', () => {
    it('never turns an empty 4xx body into a blank message', async () => {
      const pending = firstValueFrom(http.get('/api/quotes/9999')).catch((e: unknown) => e);
      httpTesting.expectOne('/api/quotes/9999').flush('', { status: 404, statusText: 'Not Found' });

      const apiError = toApiError((await pending) as HttpErrorResponse);
      expect(apiError.kind).toBe('not-found');
      expect(apiError.friendlyMessage.trim().length).toBeGreaterThan(0);
    });

    it('never surfaces the text/plain stack trace as a user-facing message', async () => {
      const stackTrace = RECORDED_ERROR_RESPONSES.malformedJson.bodyStartsWith;
      const pending = firstValueFrom(http.post('/api/auth/login', {})).catch((e: unknown) => e);
      httpTesting.expectOne('/api/auth/login').flush(stackTrace, {
        status: 400,
        statusText: 'Bad Request',
        headers: { 'Content-Type': 'text/plain; charset=utf-8' },
      });

      const apiError = toApiError((await pending) as HttpErrorResponse);
      expect(apiError.friendlyMessage).not.toContain('Exception');
      expect(apiError.friendlyMessage).not.toContain('Microsoft.AspNetCore');
      expect(apiError.friendlyMessage).toBe('The Quotes API could not accept that request.');
    });

    it('is ready for ProblemDetails the day the server starts sending it', async () => {
      const pending = firstValueFrom(http.post('/api/quotes', {})).catch((e: unknown) => e);
      httpTesting.expectOne('/api/quotes').flush(
        {
          type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
          title: 'One or more validation errors occurred.',
          status: 400,
          traceId: '00-abc-def-01',
          errors: { Author: ['Author must be between 1 and 200 characters.'] },
        },
        {
          status: 400,
          statusText: 'Bad Request',
          headers: { 'Content-Type': 'application/problem+json; charset=utf-8' },
        },
      );

      const apiError = toApiError((await pending) as HttpErrorResponse);
      expect(apiError.kind).toBe('validation');
      expect(apiError.traceId).toBe('00-abc-def-01');
      expect(apiError.fieldErrors.get('Author')).toEqual([
        'Author must be between 1 and 200 characters.',
      ]);
      expect(apiError.friendlyMessage).toBe('One or more validation errors occurred.');
    });
  });
});
