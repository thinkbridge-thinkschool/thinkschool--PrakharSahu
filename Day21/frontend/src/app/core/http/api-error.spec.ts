import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';

import { ApiError, toApiError } from './api-error';

function failure(init: {
  status: number;
  body?: unknown;
  contentType?: string;
}): HttpErrorResponse {
  return new HttpErrorResponse({
    status: init.status,
    statusText: 'x',
    error: init.body ?? null,
    url: '/api/quotes',
    headers: init.contentType ? new HttpHeaders({ 'Content-Type': init.contentType }) : undefined,
  });
}

describe('toApiError', () => {
  describe('status to kind, for the statuses this API actually returns', () => {
    const cases: readonly [number, string][] = [
      [0, 'offline'],
      [400, 'validation'],
      [401, 'unauthorized'],
      [403, 'forbidden'],
      [404, 'not-found'],
      [409, 'conflict'],
      [429, 'rate-limited'],
      [500, 'server'],
      [503, 'server'],
      [418, 'unknown'],
    ];

    it.each(cases)('maps %i to %s', (status, kind) => {
      expect(toApiError(failure({ status })).kind).toBe(kind);
    });

    it('gives every one of them a non-empty, human-readable message', () => {
      for (const [status] of cases) {
        const message = toApiError(failure({ status })).friendlyMessage;
        expect(message.trim().length, `status ${status}`).toBeGreaterThan(0);
        expect(message, `status ${status}`).toMatch(/[a-z]/);
      }
    });

    it('marks only the failures worth retrying as retryable', () => {
      expect(toApiError(failure({ status: 0 })).retryable).toBe(true);
      expect(toApiError(failure({ status: 429 })).retryable).toBe(true);
      expect(toApiError(failure({ status: 500 })).retryable).toBe(true);
      expect(toApiError(failure({ status: 404 })).retryable).toBe(false);
      expect(toApiError(failure({ status: 400 })).retryable).toBe(false);
    });
  });

  describe('bodies it must not trust', () => {
    // The only 400 the real API produces from outside.
    it('ignores a text/plain .NET exception entirely', () => {
      const error = toApiError(
        failure({
          status: 400,
          body:
            'Microsoft.AspNetCore.Http.BadHttpRequestException: Failed to read parameter ' +
            '"LoginRequest request" from the request body as JSON. ---> System.Text.Json.JsonException',
          contentType: 'text/plain; charset=utf-8',
        }),
      );

      expect(error.friendlyMessage).toBe('The Quotes API could not accept that request.');
      expect(error.friendlyMessage).not.toContain('Exception');
      expect(error.friendlyMessage).not.toContain('System.Text.Json');
    });

    it('ignores an empty body, which is what every 401/403/404 here sends', () => {
      for (const status of [401, 403, 404]) {
        const error = toApiError(failure({ status, body: '' }));
        expect(error.friendlyMessage.trim().length).toBeGreaterThan(0);
      }
    });

    it('does not mistake a bare array for problem details', () => {
      const error = toApiError(failure({ status: 400, body: ['nope'] }));
      expect(error.friendlyMessage).toBe('The Quotes API could not accept that request.');
    });
  });

  describe('bodies it should use', () => {
    // Results.BadRequest(DomainError) — reachable once the write endpoint is usable.
    it('shows a DomainError { message } verbatim', () => {
      const error = toApiError(
        failure({
          status: 400,
          body: { message: 'Text must be between 1 and 1000 characters.' },
          contentType: 'application/json',
        }),
      );
      expect(error.friendlyMessage).toBe('Text must be between 1 and 1000 characters.');
      expect(error.kind).toBe('validation');
    });

    it('prefers ProblemDetails.detail over the title', () => {
      const error = toApiError(
        failure({
          status: 409,
          body: { title: 'Conflict', detail: 'This quote was changed by someone else.' },
          contentType: 'application/problem+json',
        }),
      );
      expect(error.friendlyMessage).toBe('This quote was changed by someone else.');
    });

    it('collects ValidationProblemDetails field errors and the traceId', () => {
      const error = toApiError(
        failure({
          status: 400,
          contentType: 'application/problem+json',
          body: {
            title: 'One or more validation errors occurred.',
            traceId: '00-trace-01',
            errors: {
              Author: ['Author must be between 1 and 200 characters.'],
              Text: ['Text is required.', 'Text must be between 1 and 1000 characters.'],
            },
          },
        }),
      );

      expect(error.kind).toBe('validation');
      expect(error.traceId).toBe('00-trace-01');
      expect(error.fieldErrors.size).toBe(2);
      expect(error.fieldErrors.get('Text')).toHaveLength(2);
    });

    it('leaves fieldErrors empty for every non-validation failure', () => {
      for (const status of [401, 403, 404, 500]) {
        expect(toApiError(failure({ status })).fieldErrors.size).toBe(0);
      }
    });
  });

  it('keeps the original failure as cause for logging, without rendering it', () => {
    const original = failure({ status: 500 });
    const error = toApiError(original);

    expect(error).toBeInstanceOf(ApiError);
    expect(error.cause).toBe(original);
    expect(error.message).toBe(error.friendlyMessage);
  });
});
