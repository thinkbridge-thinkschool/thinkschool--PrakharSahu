import { HttpErrorResponse } from '@angular/common/http';

import {
  parseDomainErrorMessage,
  parseProblemDetails,
  parseValidationErrors,
} from './problem-details';

/** The closed set of failures the rest of the app is allowed to reason about. */
export type ApiErrorKind =
  | 'offline'
  | 'unauthorized'
  | 'forbidden'
  | 'not-found'
  | 'validation'
  | 'conflict'
  | 'rate-limited'
  | 'server'
  | 'unknown';

/**
 * One typed error for the whole app.
 *
 * Callers switch on `kind` and render `friendlyMessage`. They never see an
 * `HttpErrorResponse`, never read a status code, and never touch the raw body — which
 * matters here because one of this API's bodies is a .NET stack trace.
 */
export class ApiError extends Error {
  readonly kind: ApiErrorKind;
  /** HTTP status, or 0 when the request never reached the server. */
  readonly status: number;
  /** Safe to render. Never contains a raw response body. */
  readonly friendlyMessage: string;
  /** Field name → messages. Only ever populated for `kind === 'validation'`. */
  readonly fieldErrors: ReadonlyMap<string, readonly string[]>;
  readonly traceId: string | null;
  /** Whether retrying the same request unchanged could plausibly succeed. */
  readonly retryable: boolean;
  /** The original failure, for logging. Never rendered. */
  override readonly cause: unknown;

  constructor(init: {
    kind: ApiErrorKind;
    status: number;
    friendlyMessage: string;
    fieldErrors?: ReadonlyMap<string, readonly string[]>;
    traceId?: string | null;
    retryable?: boolean;
    cause?: unknown;
  }) {
    super(init.friendlyMessage);
    this.name = 'ApiError';
    this.kind = init.kind;
    this.status = init.status;
    this.friendlyMessage = init.friendlyMessage;
    this.fieldErrors = init.fieldErrors ?? new Map();
    this.traceId = init.traceId ?? null;
    this.retryable = init.retryable ?? false;
    this.cause = init.cause;
  }
}

/**
 * Maps a failed response onto an `ApiError`.
 *
 * Message precedence, most trustworthy first:
 *   1. `ValidationProblemDetails.errors` — field messages the server wrote.
 *   2. `ProblemDetails.detail` / `.title`.
 *   3. A `{ "message": "..." }` DomainError body.
 *   4. A hard-coded sentence for the status.
 *
 * A `text/plain` body never becomes a message at any level. The only one this API produces
 * is `Microsoft.AspNetCore.Http.BadHttpRequestException: ...` with a stack trace.
 */
export function toApiError(response: HttpErrorResponse): ApiError {
  const problem = parseProblemDetails(response);
  const fieldErrors = parseValidationErrors(response);
  const serverMessage =
    problem?.detail ?? problem?.title ?? parseDomainErrorMessage(response) ?? null;
  const traceId = problem?.traceId ?? null;

  const build = (kind: ApiErrorKind, fallback: string, retryable = false) =>
    new ApiError({
      kind,
      status: response.status,
      friendlyMessage: serverMessage ?? fallback,
      fieldErrors,
      traceId,
      retryable,
      cause: response,
    });

  // Status 0 means the browser never got a response: offline, DNS, refused, or CORS.
  if (response.status === 0) {
    return new ApiError({
      kind: 'offline',
      status: 0,
      friendlyMessage:
        'Could not reach the Quotes API. Check your connection, or that the server is running on port 5267.',
      traceId,
      retryable: true,
      cause: response,
    });
  }

  switch (response.status) {
    case 400:
      return fieldErrors.size > 0
        ? build('validation', 'Some of the details you entered are not valid.')
        : build('validation', 'The Quotes API could not accept that request.');
    case 401:
      return build('unauthorized', 'Your session has expired. Sign in and try again.');
    case 403:
      return build('forbidden', 'You do not have permission to do that.');
    case 404:
      return build('not-found', 'That quote no longer exists.');
    case 409:
      return build('conflict', 'That change conflicts with a more recent one. Reload and try again.');
    case 429:
      return build('rate-limited', 'Too many requests. Wait a moment and try again.', true);
    default:
      break;
  }

  if (response.status >= 500) {
    return build('server', 'The Quotes API is having trouble. Try again shortly.', true);
  }
  return build('unknown', `The request failed with status ${response.status}.`);
}
