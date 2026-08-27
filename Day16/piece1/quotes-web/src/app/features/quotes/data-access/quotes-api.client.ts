import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiError } from '../../../core/http/api-error';
import { QUOTES_API_BASE_URL } from '../../../core/config/quotes-api.config';
import { Quote, isQuote, isQuoteArray } from '../domain/quote';

/**
 * Transport for the Week-1 QuotesApi.
 *
 * Owns the URL and the response-shape check, and nothing else. Auth headers, retries and
 * error mapping are all interceptor concerns, which is why none of them appear here — this
 * class is what is left once the cross-cutting parts move out.
 *
 * Every failure that escapes is an `ApiError`; the interceptor guarantees it.
 */
@Injectable({ providedIn: 'root' })
export class QuotesApiClient {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(QUOTES_API_BASE_URL);

  /**
   * `GET /api/quotes` — 200 with a bare array.
   *
   * No paging parameters: the endpoint accepts none, and the characterization spec pins
   * that `?page=&size=` is silently ignored. Adding them here would build a pager on top
   * of something the server does not implement.
   */
  async listQuotes(): Promise<Quote[]> {
    const body = await firstValueFrom(this.http.get<unknown>(`${this.baseUrl}/quotes`));

    if (!isQuoteArray(body)) {
      throw new ApiError({
        kind: 'unknown',
        status: 200,
        friendlyMessage: 'The Quotes API returned something this app does not understand.',
        cause: body,
      });
    }
    return body;
  }

  /** `GET /api/quotes/{id:int}` — 200, or 404 for an unknown, deleted, or non-integer id. */
  async getQuote(id: number): Promise<Quote> {
    const body = await firstValueFrom(this.http.get<unknown>(`${this.baseUrl}/quotes/${id}`));

    if (!isQuote(body)) {
      throw new ApiError({
        kind: 'unknown',
        status: 200,
        friendlyMessage: 'The Quotes API returned something this app does not understand.',
        cause: body,
      });
    }
    return body;
  }
}
