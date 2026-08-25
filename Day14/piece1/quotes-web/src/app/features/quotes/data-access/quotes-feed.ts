import { Signal } from '@angular/core';

import { Quote } from '../domain/quote';

/**
 * The read seam between the state layer and whatever is fetching quotes.
 *
 * `QuotesStore` depends on this, not on `HttpClient`, so store tests can hand it a plain
 * object of signals instead of standing up an HTTP stack. `QuotesApiClient` is the single
 * production implementation.
 */
export interface QuotesFeed {
  /** Latest successfully loaded quotes; `[]` before the first response and while erroring. */
  readonly quotes: Signal<readonly Quote[]>;
  /** True during the first load and during a `refresh()`. */
  readonly isLoading: Signal<boolean>;
  /** Set when the last load failed, cleared once a load succeeds. */
  readonly failure: Signal<Error | undefined>;
  /** Re-issues the request against the same URL. */
  refresh(): void;
}
