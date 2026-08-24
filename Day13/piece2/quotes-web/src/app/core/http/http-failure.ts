import { HttpErrorResponse } from '@angular/common/http';

/**
 * Turns whatever a failed request surfaced into a sentence a user can act on.
 *
 * `httpResource().error()` is typed as `Error`, and Angular wraps non-`Error` throwables
 * (an `HttpErrorResponse` is one) so the original lands on `cause`. Both shapes are
 * unwrapped here so callers never have to care which one they got.
 */
export function describeHttpFailure(failure: unknown): string {
  const response = asHttpErrorResponse(failure);

  if (!response) {
    return failure instanceof Error && failure.message
      ? failure.message
      : 'Something went wrong while loading quotes.';
  }

  switch (response.status) {
    case 0:
      return 'Could not reach the Quotes API. Start it with `dotnet run` in Day5/piece6/QuotesApi.';
    case 401:
    case 403:
      return 'The Quotes API rejected this request as unauthorised.';
    case 404:
      return 'The quotes endpoint was not found. Check the configured API base URL.';
    default:
      return response.status >= 500
        ? `The Quotes API failed with status ${response.status}.`
        : `The request was rejected with status ${response.status}.`;
  }
}

/**
 * The HTTP status behind a failure, or `null` if it did not come from a response.
 *
 * Callers use this to branch on statuses that are part of the contract rather than
 * genuine faults — a 404 from `GET /api/quotes/{id}` is an answer, not an outage.
 */
export function httpStatusOf(failure: unknown): number | null {
  return asHttpErrorResponse(failure)?.status ?? null;
}

function asHttpErrorResponse(failure: unknown): HttpErrorResponse | null {
  if (failure instanceof HttpErrorResponse) {
    return failure;
  }
  const cause = failure instanceof Error ? failure.cause : undefined;
  return cause instanceof HttpErrorResponse ? cause : null;
}
