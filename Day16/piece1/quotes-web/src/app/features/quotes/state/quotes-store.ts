import { Injectable, computed, inject, signal } from '@angular/core';

import { ApiError } from '../../../core/http/api-error';
import { QuotesApiClient } from '../data-access/quotes-api.client';
import { Quote } from '../domain/quote';

/** The mutually exclusive things the page can be showing. */
export type QuotesViewStatus = 'idle' | 'loading' | 'ready' | 'empty' | 'failed';

interface QuotesState {
  readonly status: QuotesViewStatus;
  readonly quotes: readonly Quote[];
  readonly error: ApiError | null;
  /** What was attempted, so the failure banner can say which call broke. */
  readonly lastAction: string;
}

const INITIAL: QuotesState = { status: 'idle', quotes: [], error: null, lastAction: '' };

/**
 * Page state for the quotes list.
 *
 * Notice how little error handling is here: the store catches `ApiError` and renders
 * `friendlyMessage`. It never inspects a status code, never reads a response body, and has
 * no idea a retry happened. That is the whole return on the interceptor layer.
 */
@Injectable({ providedIn: 'root' })
export class QuotesStore {
  private readonly api = inject(QuotesApiClient);
  private readonly state = signal<QuotesState>(INITIAL);

  readonly status = computed(() => this.state().status);
  readonly quotes = computed(() => this.state().quotes);
  readonly error = computed(() => this.state().error);
  readonly lastAction = computed(() => this.state().lastAction);
  readonly isLoading = computed(() => this.state().status === 'loading');

  /** `GET /api/quotes` — the happy path, plus the empty and failed states. */
  async load(): Promise<void> {
    await this.run('GET /api/quotes', async () => {
      const quotes = await this.api.listQuotes();
      this.state.set({
        status: quotes.length === 0 ? 'empty' : 'ready',
        quotes,
        error: null,
        lastAction: 'GET /api/quotes',
      });
    });
  }

  /**
   * `GET /api/quotes/{id}` for an id that does not exist.
   *
   * Here so the 404 path can be exercised from the UI: the real API answers with a
   * completely empty body, and the point is that a useful sentence still reaches the user.
   */
  async loadMissing(id: number): Promise<void> {
    await this.run(`GET /api/quotes/${id}`, async () => {
      await this.api.getQuote(id);
      this.state.set({ ...this.state(), status: 'ready', error: null });
    });
  }

  reset(): void {
    this.state.set(INITIAL);
  }

  private async run(action: string, work: () => Promise<void>): Promise<void> {
    this.state.set({ ...this.state(), status: 'loading', error: null, lastAction: action });
    try {
      await work();
    } catch (failure) {
      // Anything reaching here is already an ApiError — the mapping interceptor guarantees
      // it. The `instanceof` check exists so a future bug surfaces loudly instead of
      // rendering "[object Object]".
      const error =
        failure instanceof ApiError
          ? failure
          : new ApiError({
              kind: 'unknown',
              status: 0,
              friendlyMessage: 'Something went wrong.',
              cause: failure,
            });
      this.state.set({ ...this.state(), status: 'failed', error, lastAction: action });
    }
  }
}
