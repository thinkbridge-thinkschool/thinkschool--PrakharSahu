import { Routes } from '@angular/router';

import { authGuard } from '../../core/auth/auth.guard';
import { quoteIdMustBeInteger, quoteTitle } from './routing/quote-id.guard';

/**
 * The quotes feature owns its own routes, mounted by the root config at `/quotes`.
 *
 * `loadChildren` on the parent means this file — and the guards it pulls in — is itself a
 * chunk, fetched the first time anyone enters the feature. Each child still has its own
 * `loadComponent`, so entering the list does not drag the detail or create pages with it.
 *
 * ORDER IS LOAD-BEARING. `new` must be declared before `:id`: the router matches top-down
 * and `:id` happily matches the literal segment `new`. Swapped, /quotes/new resolves to the
 * detail route with id="new", the id guard rejects it, and the create page 404s.
 */
export const quotesRoutes: Routes = [
  {
    path: '',
    title: 'Quotes',
    loadComponent: () => import('./ui/quotes-page/quotes-page').then((m) => m.QuotesPage),
    // `q` is bound straight onto the component as a signal input. Re-running the guards and
    // resolvers on a query-only change keeps the title honest when the filter changes.
    runGuardsAndResolvers: 'paramsOrQueryParamsChange',
  },

  // Static segment first — see the note above.
  {
    path: 'new',
    title: 'New quote',
    canActivate: [authGuard],
    loadComponent: () => import('./ui/new-quote-page/new-quote-page').then((m) => m.NewQuotePage),
  },

  {
    path: ':id',
    // A resolved title, so the browser tab and the history entry say which quote this is
    // rather than a generic word. Derived from the route param, not fetched — a resolver
    // that fetched would duplicate the request the page is already making.
    title: quoteTitle,
    // Rejects ids the API's {id:int} route could never serve, so the url falls through to
    // the wildcard instead of firing a request that is certain to 404.
    canMatch: [quoteIdMustBeInteger],
    loadComponent: () =>
      import('./ui/quote-detail-page/quote-detail-page').then((m) => m.QuoteDetailPage),
  },
];
