import { ActivatedRouteSnapshot, CanMatchFn, ResolveFn, Route, UrlSegment } from '@angular/router';

/**
 * The route parameter as the API defines it.
 *
 * `GET /api/quotes/{id:int}` — the `:int` constraint is part of the server's route, and a
 * non-integer never reaches the handler: the recorded response for `/api/quotes/abc` is a
 * 404 from routing itself, not a 400 from validation.
 */
export function isValidQuoteId(raw: string | undefined | null): raw is string {
  if (raw === undefined || raw === null) {
    return false;
  }
  // Deliberately strict. `Number('1.5')` and `Number(' 1 ')` are both finite, and
  // `parseInt('12abc')` is 12 — none of those are ids this API would accept.
  return /^\d+$/.test(raw) && Number.isSafeInteger(Number(raw)) && Number(raw) > 0;
}

/**
 * Keeps the detail route from matching a url whose id the API could never serve.
 *
 * `canMatch` rather than `canActivate` on purpose: returning false here means the route
 * does not match at all, so the router keeps looking and the request falls through to the
 * wildcard and the not-found page. With `canActivate` the navigation would simply be
 * cancelled and the user would be left staring at the previous page.
 */
export const quoteIdMustBeInteger: CanMatchFn = (_route: Route, segments: UrlSegment[]) => {
  // Mounted at /quotes, so the child route's own segments are just ['<id>'].
  const idSegment = segments[segments.length - 1]?.path;
  return isValidQuoteId(idSegment);
};

/**
 * Names the history entry and the browser tab after the quote being viewed.
 *
 * Derived from the param rather than fetched: a resolver that called the API would double
 * the request the page already makes, and would block the navigation until it answered.
 */
export const quoteTitle: ResolveFn<string> = (route: ActivatedRouteSnapshot) =>
  `Quote #${route.paramMap.get('id')} · Quotes`;
