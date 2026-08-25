import { httpResource } from '@angular/common/http';
import { Injectable, computed, inject } from '@angular/core';

import { QUOTES_API_BASE_URL } from '../../../core/config/quotes-api.config';
import { MalformedQuoteError, Quote, isQuoteArray } from '../domain/quote';
import { QuotesFeed } from './quotes-feed';

/**
 * Transport for the Week-1 QuotesApi. Owns the endpoint path and nothing else — no
 * filtering, no sorting, no formatting.
 *
 * `httpResource` is Angular's signal-native fetch primitive: it issues the request when
 * its reactive URL is first read and exposes `value`/`isLoading`/`error` as signals, so
 * nothing here has to subscribe or unsubscribe. It is still marked experimental upstream;
 * swapping it for `HttpClient.get()` behind this class would not touch any other layer.
 */
@Injectable({ providedIn: 'root' })
export class QuotesApiClient implements QuotesFeed {
  private readonly baseUrl = inject(QUOTES_API_BASE_URL);

  /** `GET {baseUrl}/quotes` — anonymous on the Week-1 API, so no auth header is attached. */
  private readonly resource = httpResource<Quote[]>(() => `${this.baseUrl}/quotes`, {
    defaultValue: [],
  });

  /**
   * `resource.value()` *throws* a `ResourceValueError` while the resource is in its error
   * state — `defaultValue` only covers `idle` and `loading`. Reading it straight would take
   * the whole page down on the first failed request, so the error state is folded into an
   * empty list here and reported separately through `failure`.
   */
  private readonly body = computed<unknown>(() =>
    this.resource.hasValue() ? this.resource.value() : [],
  );

  /** Only ever a real array of real quotes; anything else is reported through `failure`. */
  readonly quotes = computed<readonly Quote[]>(() => {
    const value = this.body();
    return isQuoteArray(value) ? value : [];
  });

  readonly isLoading = this.resource.isLoading;

  /**
   * Angular passes error-like throwables through untouched, so a transport failure is the
   * `HttpErrorResponse` itself rather than a wrapper — see `describeHttpFailure`. A 200
   * that is not an array of quotes is reported here too: the alternative is an empty list
   * that looks exactly like "the API has no quotes".
   */
  readonly failure = computed<Error | undefined>(() => {
    const transportFailure = this.resource.error();
    if (transportFailure) {
      return transportFailure;
    }
    return isQuoteArray(this.body()) ? undefined : new MalformedQuoteError('a list of quotes');
  });

  refresh(): void {
    this.resource.reload();
  }
}
