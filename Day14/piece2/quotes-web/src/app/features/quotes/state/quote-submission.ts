import { Injectable, computed, inject, signal } from '@angular/core';

import { CreateQuoteClient } from '../data-access/create-quote.client';
import { CreateQuoteRequest } from '../domain/create-quote';
import { Quote } from '../domain/quote';

/** Where a submit ended up. Each arm needs different copy, so none of them is a boolean. */
export type SubmissionState =
  | { readonly kind: 'editing' }
  | { readonly kind: 'submitting' }
  | { readonly kind: 'created'; readonly quote: Quote }
  | { readonly kind: 'rejected'; readonly message: string }
  | { readonly kind: 'unauthenticated' }
  | { readonly kind: 'forbidden' }
  | { readonly kind: 'failed'; readonly message: string };

/**
 * Everything that happens *after* a form is judged valid: send it, and remember how the
 * server answered.
 *
 * Deliberately shared by both the Signal Forms and the reactive-forms implementations, so
 * the only difference between them is how a form is described and validated. Anything the
 * two versions have in common should live here rather than being written twice and then
 * compared as if it were a difference.
 *
 * Not `providedIn: 'root'` — each form provides its own, so two forms on one page cannot
 * overwrite each other's outcome.
 */
@Injectable()
export class QuoteSubmission {
  private readonly api = inject(CreateQuoteClient);

  private readonly state = signal<SubmissionState>({ kind: 'editing' });

  readonly submissionState = this.state.asReadonly();
  readonly isSubmitting = computed(() => this.state().kind === 'submitting');
  readonly needsFocus = computed(() => {
    const kind = this.state().kind;
    return kind === 'rejected' || kind === 'unauthenticated' || kind === 'forbidden' || kind === 'failed';
  });

  /** Sends the request and records the outcome. Returns it so callers can branch once. */
  async send(request: CreateQuoteRequest): Promise<SubmissionState> {
    this.state.set({ kind: 'submitting' });

    const outcome = await this.api.create(request);
    const next: SubmissionState =
      outcome.status === 'created'
        ? { kind: 'created', quote: outcome.quote }
        : outcome.status === 'rejected'
          ? { kind: 'rejected', message: outcome.message }
          : outcome.status === 'unauthenticated'
            ? { kind: 'unauthenticated' }
            : outcome.status === 'forbidden'
              ? { kind: 'forbidden' }
              : { kind: 'failed', message: outcome.message };

    this.state.set(next);
    return next;
  }

  /** Clears a previous outcome so a stale banner cannot sit above a new attempt. */
  clear(): void {
    this.state.set({ kind: 'editing' });
  }
}
