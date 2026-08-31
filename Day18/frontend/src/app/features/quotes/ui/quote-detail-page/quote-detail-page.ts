import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';

import { ApiError } from '../../../../core/http/api-error';
import { QuotesApiClient } from '../../data-access/quotes-api.client';
import { Quote } from '../../domain/quote';
import { isValidQuoteId } from '../../routing/quote-id.guard';
import { signal } from '@angular/core';

type DetailStatus = 'loading' | 'ready' | 'failed';

/**
 * The detail route, lazy-loaded as its own chunk.
 *
 * `id` arrives as a signal input because the router is configured with
 * `withComponentInputBinding()`. That matters for more than tidiness: navigating from
 * /quotes/1 to /quotes/2 REUSES this component instance rather than recreating it, so
 * reading the param once in a constructor would leave the page showing quote 1 forever.
 * The effect below re-runs on every id change.
 */
@Component({
  selector: 'app-quote-detail-page',
  imports: [DatePipe, RouterLink],
  templateUrl: './quote-detail-page.html',
  styleUrl: './quote-detail-page.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuoteDetailPage {
  private readonly api = inject(QuotesApiClient);

  /** Bound from the `:id` route parameter. Always a string — route params are text. */
  readonly id = input.required<string>();

  private readonly quoteState = signal<{
    status: DetailStatus;
    quote: Quote | null;
    error: ApiError | null;
  }>({ status: 'loading', quote: null, error: null });

  protected readonly status = computed(() => this.quoteState().status);
  protected readonly quote = computed(() => this.quoteState().quote);
  protected readonly error = computed(() => this.quoteState().error);

  /** Pairs with the list card of the same id so the browser can animate between them. */
  protected readonly transitionName = computed(() => `quote-${this.id()}`);

  constructor() {
    effect(() => {
      const raw = this.id();
      // The canMatch guard already rejected non-integers, so this is belt and braces — but
      // the component is public API and a test can instantiate it directly.
      if (!isValidQuoteId(raw)) {
        this.quoteState.set({
          status: 'failed',
          quote: null,
          error: new ApiError({
            kind: 'not-found',
            status: 404,
            friendlyMessage: 'That quote id is not valid.',
          }),
        });
        return;
      }
      void this.load(Number(raw));
    });
  }

  private async load(id: number): Promise<void> {
    this.quoteState.set({ status: 'loading', quote: null, error: null });
    try {
      const quote = await this.api.getQuote(id);
      this.quoteState.set({ status: 'ready', quote, error: null });
    } catch (failure) {
      const error =
        failure instanceof ApiError
          ? failure
          : new ApiError({ kind: 'unknown', status: 0, friendlyMessage: 'Something went wrong.' });
      this.quoteState.set({ status: 'failed', quote: null, error });
    }
  }
}
