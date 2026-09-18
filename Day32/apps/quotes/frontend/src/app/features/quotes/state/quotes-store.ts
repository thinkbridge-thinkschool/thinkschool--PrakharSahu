import { Injectable, computed, inject, signal } from '@angular/core';

import { ApiError } from '../../../core/http/api-error';
import { TokenStore } from '../../../core/auth/token-store';
import { QuotesApiClient } from '../data-access/quotes-api.client';
import { Quote } from '../domain/quote';

/** The mutually exclusive things the list can be showing. */
export type QuotesViewStatus = 'idle' | 'loading' | 'ready' | 'empty' | 'failed';

/** A delete that failed, kept per quote so two failures do not overwrite each other. */
export interface RemovalFailure {
  readonly id: number;
  readonly message: string;
}

/**
 * State for the quotes feature: signals and a service, no store library.
 *
 * WHY NO STORE LIBRARY HERE — the rule this codebase follows is written out in
 * `docs/when-to-adopt-a-store.md`. In short: one feature owns this slice, it holds tens of
 * rows rather than thousands, and nothing outside it writes. None of the thresholds are met,
 * so the extra indirection would cost more than it returns.
 *
 * THE SHAPE. Only four things are writable:
 *
 *   serverQuotes  what GET /api/quotes last returned
 *   status        where the load got to
 *   removing      ids whose DELETE is in flight — the optimistic layer
 *   failures      deletes that came back refused
 *
 * Everything the template reads is `computed` from those. That is what makes the concurrent
 * cases fall out rather than needing to be coordinated: a refresh landing mid-delete
 * replaces `serverQuotes` without touching `removing`, so the row the user just dismissed
 * does not flicker back into the list.
 */
@Injectable({ providedIn: 'root' })
export class QuotesStore {
  private readonly api = inject(QuotesApiClient);
  private readonly tokens = inject(TokenStore);

  private readonly serverQuotes = signal<readonly Quote[]>([]);
  private readonly status = signal<QuotesViewStatus>('idle');
  private readonly loadError = signal<ApiError | null>(null);
  private readonly removing = signal<ReadonlySet<number>>(new Set());
  private readonly failures = signal<readonly RemovalFailure[]>([]);

  /**
   * Guards against out-of-order load responses.
   *
   * Two refreshes in flight can answer in either order; without this the slower one wins
   * simply by arriving last and the list silently goes stale. Every load takes a ticket and
   * only the newest ticket is allowed to write.
   */
  private loadToken = 0;

  // ---- what the UI reads -------------------------------------------------------------

  /** The list to render: server truth minus anything optimistically removed. */
  readonly quotes = computed(() => {
    const hidden = this.removing();
    return this.serverQuotes().filter((quote) => !hidden.has(quote.id));
  });

  readonly viewStatus = computed<QuotesViewStatus>(() => {
    const status = this.status();
    if (status !== 'ready') {
      return status;
    }
    // "Everything on screen has been deleted" is empty, not ready-with-nothing.
    return this.quotes().length === 0 ? 'empty' : 'ready';
  });

  readonly isLoading = computed(() => this.status() === 'loading');
  readonly error = computed(() => this.loadError());
  readonly removalFailures = computed(() => this.failures());
  readonly pendingRemovals = computed(() => this.removing().size);
  readonly canRemove = this.tokens.isSignedIn;

  /** True while this specific row is being deleted — for per-row busy state. */
  isRemoving(id: number): boolean {
    return this.removing().has(id);
  }

  // ---- commands ----------------------------------------------------------------------

  /**
   * `GET /api/quotes`.
   *
   * Safe to call concurrently: late responses from superseded calls are dropped rather than
   * applied. The status only drops to 'loading' when there is nothing on screen yet, so a
   * background refresh does not blank a list the user is reading.
   */
  async load(): Promise<void> {
    const ticket = ++this.loadToken;

    if (this.serverQuotes().length === 0) {
      this.status.set('loading');
    }
    this.loadError.set(null);

    try {
      const quotes = await this.api.listQuotes();
      if (ticket !== this.loadToken) {
        return; // superseded by a newer load
      }
      this.serverQuotes.set(quotes);
      this.status.set(quotes.length === 0 ? 'empty' : 'ready');
      // A successful refresh is the server's word on what exists, so stale complaints about
      // rows that are no longer there should not survive it.
      this.failures.set([]);
    } catch (failure) {
      if (ticket !== this.loadToken) {
        return;
      }
      this.loadError.set(this.asApiError(failure));
      this.status.set('failed');
    }
  }

  /**
   * `DELETE /api/quotes/{id}`, applied optimistically.
   *
   * The row disappears immediately and comes back if the server refuses — which it will
   * with a 403 for a quote this user does not own, a case the seeded data reaches for real.
   * Two removals can be in flight at once; each is tracked by id, so one failing restores
   * only its own row.
   */
  async remove(id: number): Promise<void> {
    if (this.removing().has(id)) {
      return; // already going; a second click must not fire a second DELETE
    }

    this.hide(id);
    this.failures.update((all) => all.filter((failure) => failure.id !== id));

    try {
      await this.api.deleteQuote(id);
      // Confirmed: drop it from server truth, then stop hiding it. Order matters — reveal
      // first and the row would flash back for a frame before the removal lands.
      this.serverQuotes.update((quotes) => quotes.filter((quote) => quote.id !== id));
      this.reveal(id);
    } catch (failure) {
      const error = this.asApiError(failure);
      this.reveal(id);
      this.failures.update((all) => [
        ...all,
        { id, message: this.explainRemoval(id, error) },
      ]);
    }
  }

  dismissFailure(id: number): void {
    this.failures.update((all) => all.filter((failure) => failure.id !== id));
  }

  reset(): void {
    this.loadToken += 1; // invalidate anything in flight
    this.serverQuotes.set([]);
    this.status.set('idle');
    this.loadError.set(null);
    this.removing.set(new Set());
    this.failures.set([]);
  }

  // ---- internals ---------------------------------------------------------------------

  private hide(id: number): void {
    this.removing.update((ids) => new Set(ids).add(id));
  }

  private reveal(id: number): void {
    this.removing.update((ids) => {
      const next = new Set(ids);
      next.delete(id);
      return next;
    });
  }

  /** Turns the transport's typed error into a sentence that names the row. */
  private explainRemoval(id: number, error: ApiError): string {
    switch (error.kind) {
      case 'forbidden':
        return `Quote #${id} belongs to someone else, so it was not deleted.`;
      case 'unauthorized':
        return `Your session expired before quote #${id} could be deleted. Sign in and try again.`;
      case 'not-found':
        return `Quote #${id} had already been deleted.`;
      default:
        return `Quote #${id} could not be deleted. ${error.friendlyMessage}`;
    }
  }

  private asApiError(failure: unknown): ApiError {
    // The mapping interceptor guarantees this; the check exists so a future bug surfaces
    // loudly instead of rendering "[object Object]".
    return failure instanceof ApiError
      ? failure
      : new ApiError({
          kind: 'unknown',
          status: 0,
          friendlyMessage: 'Something went wrong.',
          cause: failure,
        });
  }
}
