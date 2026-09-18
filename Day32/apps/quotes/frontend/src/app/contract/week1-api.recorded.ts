/**
 * Responses recorded from the live Week-1 API, byte for byte.
 *
 * Captured 2026-08-25 with curl against `dotnet run --launch-profile http`
 * (Day5/piece6/QuotesApi) on http://localhost:5267, ASPNETCORE_ENVIRONMENT=Development.
 *
 * These are golden files, not fixtures somebody invented to make tests pass. The
 * characterization spec next to them asserts what the server ACTUALLY does today —
 * including the parts that are inconvenient — so that a change on the server side shows up
 * as a failing test here rather than as a broken screen.
 *
 * Re-record by re-running the curl commands in exercise.txt §3 and pasting the output.
 */

/** `GET /api/quotes` — 200, `application/json; charset=utf-8`. A BARE ARRAY, no envelope. */
export const RECORDED_QUOTES_200 = [
  {
    id: 1,
    text: 'First quote for week 1',
    author: 'avi@example.com',
    createdAt: '2026-08-10T10:00:00+00:00',
    isDeleted: false,
    userId: 1,
  },
  {
    id: 2,
    text: 'Second quote, I figured out the bugs',
    author: 'avi@example.com',
    createdAt: '2026-08-16T14:00:00+00:00',
    isDeleted: false,
    userId: 1,
  },
  {
    id: 3,
    text: 'Always use CTEs instead of correlated subqueries.',
    author: 'mentor@thinkbridge.com',
    createdAt: '2026-08-12T09:00:00+00:00',
    isDeleted: false,
    userId: 2,
  },
] as const;

/** Every field name the array elements carry. The brief said three; there are six. */
export const RECORDED_QUOTE_FIELDS = [
  'id',
  'text',
  'author',
  'createdAt',
  'isDeleted',
  'userId',
] as const;

/**
 * `GET /api/quotes?page=1&size=2` returned the SAME five rows as the unparameterised call.
 * The endpoint takes no paging parameters (QuoteEndpointExtensions.cs:20) and ASP.NET
 * discards unmatched query strings silently. Recorded so nobody builds a pager on top of a
 * parameter the server ignores.
 */
export const RECORDED_PAGING_IS_IGNORED = {
  requestedPage: 1,
  requestedSize: 2,
  rowsReturned: 5,
  rowsReturnedWithoutParams: 5,
} as const;

/**
 * Every 4xx this API produces, as recorded. Note what is NOT here: a single
 * `application/problem+json` body. `AddProblemDetails()` is never called, so
 * `ProblemDetails` and `ValidationProblemDetails` do not exist on this server.
 */
export const RECORDED_ERROR_RESPONSES = {
  /** `GET /api/quotes/9999` — unknown id. */
  notFound: { status: 404, contentType: null, body: '' },

  /** `GET /api/quotes/abc` — the `{id:int}` route constraint rejects it as a 404, not a 400. */
  routeConstraintMiss: { status: 404, contentType: null, body: '' },

  /** `POST /api/quotes` with no token. */
  unauthorized: { status: 401, contentType: null, body: '', wwwAuthenticate: 'Bearer' },

  /** `POST /api/quotes` with a VALID token — the policy wants a `quotes.write` scope. */
  forbidden: { status: 403, contentType: null, body: '' },

  /** `POST /api/auth/login` with wrong credentials. */
  loginRejected: { status: 401, contentType: null, body: '' },

  /**
   * `POST /api/auth/login` with malformed JSON — the only 400 reachable from outside.
   *
   * It is `text/plain` carrying a raw .NET exception and stack trace. This body must never
   * reach a user: it is not a message, it is an implementation detail. Truncated here; the
   * full text is in the verification log.
   */
  malformedJson: {
    status: 400,
    contentType: 'text/plain; charset=utf-8',
    bodyStartsWith:
      'Microsoft.AspNetCore.Http.BadHttpRequestException: Failed to read parameter "LoginRequest request" from the request body as JSON.',
  },
} as const;

/** True if any recorded 4xx used the ProblemDetails media type. It does not. */
export const RECORDED_ANY_PROBLEM_DETAILS = false;
