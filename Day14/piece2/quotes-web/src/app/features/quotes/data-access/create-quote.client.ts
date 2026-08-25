import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { QUOTES_API_BASE_URL } from '../../../core/config/quotes-api.config';
import { CreateQuoteRequest } from '../domain/create-quote';
import { Quote, isQuote } from '../domain/quote';

/**
 * Every way `POST /api/quotes` can end, as a closed set.
 *
 * A discriminated union rather than a thrown error: each arm needs different copy and a
 * different recovery, and the compiler will not let a caller forget one.
 */
export type CreateQuoteOutcome =
  | { readonly status: 'created'; readonly quote: Quote }
  /** 400 — `Quote.Create` rejected the input. `message` is the server's own wording. */
  | { readonly status: 'rejected'; readonly message: string }
  /** 401 — no token, or it expired. */
  | { readonly status: 'unauthenticated' }
  /** 403 — authenticated, but the token lacks the `quotes.write` scope. */
  | { readonly status: 'forbidden' }
  | { readonly status: 'failed'; readonly message: string };

/** Shape of the 400 body: `Results.BadRequest(DomainError)` → `{ "message": "..." }`. */
interface DomainErrorBody {
  readonly message?: unknown;
}

/**
 * Transport for creating a quote. A command, not a resource — it runs once per submit and
 * has no reactive URL — so it uses `HttpClient` directly rather than `httpResource`.
 */
@Injectable({ providedIn: 'root' })
export class CreateQuoteClient {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(QUOTES_API_BASE_URL);

  async create(request: CreateQuoteRequest): Promise<CreateQuoteOutcome> {
    try {
      const created = await firstValueFrom(
        this.http.post<unknown>(`${this.baseUrl}/quotes`, request),
      );

      // The 201 body is the created Quote, with the server-assigned id. Validate it for
      // the same reason the read side does: the type is a claim until something checks it.
      return isQuote(created)
        ? { status: 'created', quote: created }
        : { status: 'failed', message: 'The quote was created but the response was unreadable.' };
    } catch (failure) {
      return this.describe(failure);
    }
  }

  private describe(failure: unknown): CreateQuoteOutcome {
    if (!(failure instanceof HttpErrorResponse)) {
      return { status: 'failed', message: 'The quote could not be sent.' };
    }

    switch (failure.status) {
      case 400:
        return { status: 'rejected', message: this.domainMessage(failure) };
      case 401:
        return { status: 'unauthenticated' };
      case 403:
        return { status: 'forbidden' };
      case 0:
        return {
          status: 'failed',
          message: 'Could not reach the Quotes API. Check that it is running on port 5267.',
        };
      default:
        return { status: 'failed', message: `The Quotes API failed with status ${failure.status}.` };
    }
  }

  /** Prefers the server's own sentence; falls back only when the body is not what we expect. */
  private domainMessage(failure: HttpErrorResponse): string {
    const body: unknown = failure.error;

    if (typeof body === 'string' && body.trim() !== '') {
      return body;
    }
    if (typeof body === 'object' && body !== null) {
      const message = (body as DomainErrorBody).message;
      if (typeof message === 'string' && message.trim() !== '') {
        return message;
      }
    }
    return 'The Quotes API rejected the quote.';
  }
}
