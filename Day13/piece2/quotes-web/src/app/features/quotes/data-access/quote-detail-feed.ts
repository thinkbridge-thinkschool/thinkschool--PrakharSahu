import { Signal } from '@angular/core';

import { Quote } from '../domain/quote';

/**
 * Read seam for a single quote, mirroring `QuotesFeed` for the list.
 *
 * `QuoteDetailStore` depends on this shape rather than on `HttpClient`, so its tests can
 * drive the states with plain signals.
 */
export interface QuoteDetailFeed {
  /** The loaded quote, or `undefined` when nothing is selected / loading / failed. */
  readonly quote: Signal<Quote | undefined>;
  readonly isLoading: Signal<boolean>;
  /** Set when the last load failed. A 404 arrives here too — the store separates it out. */
  readonly failure: Signal<Error | undefined>;
  /** Re-issues the request for the currently selected id. */
  reload(): void;
}
