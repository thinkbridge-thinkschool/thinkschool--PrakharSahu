import { HttpErrorResponse } from '@angular/common/http';

/**
 * RFC 9457 problem details, and the ASP.NET validation flavour.
 *
 * The Week-1 API does not produce either today — `AddProblemDetails()` is never called, and
 * every recorded 4xx is an empty body or `text/plain` (see contract/week1-api.recorded.ts).
 * These types exist so the mapper *uses* problem details the moment the server starts
 * sending them, without needing a rewrite, and so the parsing rule is written down rather
 * than implied.
 */
export interface ProblemDetails {
  readonly type?: string;
  readonly title?: string;
  readonly status?: number;
  readonly detail?: string;
  readonly instance?: string;
  readonly traceId?: string;
}

export interface ValidationProblemDetails extends ProblemDetails {
  /** ASP.NET's shape: field name → messages. */
  readonly errors?: Readonly<Record<string, readonly string[]>>;
}

const PROBLEM_JSON = 'application/problem+json';

/**
 * Extracts problem details from a failed response, or `null` when there are none.
 *
 * Deliberately strict about what it will trust:
 *
 * - The body must be an object. A `text/plain` body is never parsed, because the only
 *   `text/plain` 400 this API produces is a raw .NET exception with a stack trace, and
 *   echoing that at a user is both meaningless and a small information leak.
 * - It must either arrive as `application/problem+json`, or carry at least one field that
 *   only problem details would have. A plain `{ "message": "..." }` — which is what
 *   `Results.BadRequest(DomainError)` sends — is not problem details and is handled
 *   separately.
 */
export function parseProblemDetails(response: HttpErrorResponse): ProblemDetails | null {
  const body: unknown = response.error;
  if (typeof body !== 'object' || body === null || Array.isArray(body)) {
    return null;
  }

  const declaredAsProblem = (response.headers.get('Content-Type') ?? '').includes(PROBLEM_JSON);
  const candidate = body as Record<string, unknown>;
  const looksLikeProblem =
    typeof candidate['title'] === 'string' ||
    typeof candidate['detail'] === 'string' ||
    typeof candidate['type'] === 'string' ||
    isValidationErrorBag(candidate['errors']);

  if (!declaredAsProblem && !looksLikeProblem) {
    return null;
  }

  return {
    type: asString(candidate['type']),
    title: asString(candidate['title']),
    status: typeof candidate['status'] === 'number' ? candidate['status'] : undefined,
    detail: asString(candidate['detail']),
    instance: asString(candidate['instance']),
    traceId: asString(candidate['traceId']),
  };
}

/** Field-level messages from a `ValidationProblemDetails`, empty when there are none. */
export function parseValidationErrors(
  response: HttpErrorResponse,
): ReadonlyMap<string, readonly string[]> {
  const body: unknown = response.error;
  if (typeof body !== 'object' || body === null) {
    return new Map();
  }

  const bag = (body as Record<string, unknown>)['errors'];
  if (!isValidationErrorBag(bag)) {
    return new Map();
  }

  const entries = Object.entries(bag).map(
    ([field, messages]) => [field, messages.filter((m): m is string => typeof m === 'string')] as const,
  );
  return new Map(entries.filter(([, messages]) => messages.length > 0));
}

/**
 * The `{ "message": "..." }` body that `Results.BadRequest(DomainError)` sends.
 *
 * Not problem details, but it IS a message the server wrote for a human, so it is safe to
 * show — unlike the `text/plain` exception dump.
 */
export function parseDomainErrorMessage(response: HttpErrorResponse): string | null {
  const body: unknown = response.error;
  if (typeof body !== 'object' || body === null) {
    return null;
  }
  const message = (body as Record<string, unknown>)['message'];
  return typeof message === 'string' && message.trim() !== '' ? message : null;
}

function isValidationErrorBag(value: unknown): value is Record<string, unknown[]> {
  return (
    typeof value === 'object' &&
    value !== null &&
    !Array.isArray(value) &&
    Object.values(value).every(Array.isArray)
  );
}

function asString(value: unknown): string | undefined {
  return typeof value === 'string' ? value : undefined;
}
