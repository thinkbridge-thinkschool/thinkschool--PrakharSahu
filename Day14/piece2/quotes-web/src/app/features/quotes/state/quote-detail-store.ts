import { Injectable, computed, inject } from '@angular/core';

import { describeHttpFailure, httpStatusOf } from '../../../core/http/http-failure';
import { QuoteDetailApiClient } from '../data-access/quote-detail-api.client';
import { QuoteSelection } from './quote-selection';

/** The mutually exclusive states the detail pane can be in. */
export type QuoteDetailStatus = 'idle' | 'loading' | 'not-found' | 'failed' | 'ready';

/**
 * State for the detail pane.
 *
 * The pane has no id of its own — it follows `QuoteSelection`, so the list and the detail
 * can never disagree about which quote is open. A 404 is split out of the failure path
 * deliberately: `GET /api/quotes/{id}` returns it for an unknown, soft-deleted, or
 * non-integer id, all of which are answers about the data, not signs the API is unwell.
 */
@Injectable({ providedIn: 'root' })
export class QuoteDetailStore {
  private readonly selection = inject(QuoteSelection);
  private readonly feed = inject(QuoteDetailApiClient).watch(this.selection.selectedQuoteId);

  readonly selectedQuoteId = this.selection.selectedQuoteId;
  readonly quote = this.feed.quote;

  /** True only for a 404 — an id that is not there, as opposed to a request that broke. */
  readonly isNotFound = computed(() => httpStatusOf(this.feed.failure()) === 404);

  /** A sentence for genuine faults. `null` for a 404, which gets its own copy in the view. */
  readonly failureMessage = computed(() => {
    const failure = this.feed.failure();
    return failure && !this.isNotFound() ? describeHttpFailure(failure) : null;
  });

  readonly status = computed<QuoteDetailStatus>(() => {
    if (this.selectedQuoteId() === null) {
      return 'idle';
    }
    if (this.feed.isLoading()) {
      return 'loading';
    }
    if (this.isNotFound()) {
      return 'not-found';
    }
    if (this.failureMessage() !== null) {
      return 'failed';
    }
    return this.quote() === undefined ? 'loading' : 'ready';
  });

  select(quoteId: number): void {
    this.selection.select(quoteId);
  }

  close(): void {
    this.selection.clear();
  }

  reload(): void {
    this.feed.reload();
  }
}
