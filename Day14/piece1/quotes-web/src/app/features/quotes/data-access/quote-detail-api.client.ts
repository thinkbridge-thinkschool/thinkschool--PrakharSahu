import { httpResource } from '@angular/common/http';
import { Injectable, Signal, computed, inject } from '@angular/core';

import { QUOTES_API_BASE_URL } from '../../../core/config/quotes-api.config';
import { MalformedQuoteError, Quote, isQuote } from '../domain/quote';
import { QuoteDetailFeed } from './quote-detail-feed';

/**
 * Transport for `GET /api/quotes/{id:int}` — 200 with a single quote, or 404 when the id
 * is unknown, soft-deleted, or not an integer.
 *
 * `watch()` returns a feed that follows the supplied id signal, so the caller keeps
 * ownership of *which* quote is shown while this class keeps ownership of the URL. It
 * must be called from an injection context because it creates a resource.
 */
@Injectable({ providedIn: 'root' })
export class QuoteDetailApiClient {
  private readonly baseUrl = inject(QUOTES_API_BASE_URL);

  watch(quoteId: Signal<number | null>): QuoteDetailFeed {
    const resource = httpResource<Quote>(() => {
      const id = quoteId();
      // Returning undefined means "make no request" — the resource stays idle. Building a
      // URL out of a null id would fetch /api/quotes/null and get a 404 the user caused
      // by selecting nothing.
      return id === null ? undefined : `${this.baseUrl}/quotes/${id}`;
    });

    // Same trap as the list transport: value() throws while the resource is in its error
    // state, so a 404 would take the page down instead of rendering "not found".
    const body = computed<unknown>(() => (resource.hasValue() ? resource.value() : undefined));

    return {
      quote: computed(() => {
        const value = body();
        return isQuote(value) ? value : undefined;
      }),
      isLoading: resource.isLoading,
      failure: computed(() => {
        const transportFailure = resource.error();
        if (transportFailure) {
          return transportFailure;
        }
        // A 200 whose body is not a quote is a contract breach, not a success. Without
        // this the pane renders blank and silent: `hasValue()` is true for a JSON `null`,
        // so the state machine reads 'ready' and the template finds nothing to show.
        const value = body();
        return value !== undefined && !isQuote(value)
          ? new MalformedQuoteError('a quote')
          : undefined;
      }),
      reload: () => void resource.reload(),
    };
  }
}
