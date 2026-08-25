import { Injectable, computed, inject, signal } from '@angular/core';
import { FieldState, form, submit } from '@angular/forms/signals';

import { AccessTokenStore } from '../../../core/auth/access-token.store';
import { CreateQuoteClient } from '../data-access/create-quote.client';
import { QUOTE_AUTHOR_MAX_LENGTH, QUOTE_TEXT_MAX_LENGTH } from '../domain/create-quote';
import { Quote } from '../domain/quote';
import {
  CreateQuoteModel,
  EMPTY_CREATE_QUOTE,
  createQuoteSchema,
  remainingCharacters,
} from './create-quote.schema';

/** Stable ids so the label, the input, its errors and the summary link all agree. */
export const AUTHOR_FIELD_ID = 'create-quote-author';
export const TEXT_FIELD_ID = 'create-quote-text';

/** Where a submit ended up. Each arm needs different copy, so none of them is a boolean. */
export type SubmissionState =
  | { readonly kind: 'editing' }
  | { readonly kind: 'submitting' }
  | { readonly kind: 'created'; readonly quote: Quote }
  | { readonly kind: 'rejected'; readonly message: string }
  | { readonly kind: 'unauthenticated' }
  | { readonly kind: 'forbidden' }
  | { readonly kind: 'failed'; readonly message: string };

/** One row of the error summary: enough to render a link that focuses the offending input. */
export interface FieldErrorSummaryItem {
  readonly fieldId: string;
  readonly label: string;
  readonly message: string;
}

@Injectable({ providedIn: 'root' })
export class CreateQuoteStore {
  private readonly api = inject(CreateQuoteClient);
  private readonly tokens = inject(AccessTokenStore);

  private readonly model = signal<CreateQuoteModel>({ ...EMPTY_CREATE_QUOTE });

  readonly form = form(this.model, createQuoteSchema);

  /**
   * Errors are hidden until the user has either left a field or tried to submit. Showing
   * "Enter an author" the moment an empty form renders is noise, and a screen reader would
   * read it before the user has typed a character.
   */
  private readonly submitAttempted = signal(false);
  readonly hasAttemptedSubmit = this.submitAttempted.asReadonly();

  private readonly submission = signal<SubmissionState>({ kind: 'editing' });
  readonly submissionState = this.submission.asReadonly();

  readonly isSubmitting = computed(() => this.submission().kind === 'submitting');
  readonly isSignedIn = this.tokens.isSignedIn;

  readonly authorMaxLength = QUOTE_AUTHOR_MAX_LENGTH;
  readonly textMaxLength = QUOTE_TEXT_MAX_LENGTH;

  readonly authorRemaining = computed(() =>
    remainingCharacters(this.form.author().value(), QUOTE_AUTHOR_MAX_LENGTH),
  );
  readonly textRemaining = computed(() =>
    remainingCharacters(this.form.text().value(), QUOTE_TEXT_MAX_LENGTH),
  );

  /** Field order is DOM order — the summary and the focus target both depend on it. */
  private readonly fields: readonly { id: string; label: string; state: () => FieldState<string> }[] =
    [
      { id: AUTHOR_FIELD_ID, label: 'Author', state: () => this.form.author() },
      { id: TEXT_FIELD_ID, label: 'Quote text', state: () => this.form.text() },
    ];

  readonly errorSummary = computed<FieldErrorSummaryItem[]>(() => {
    if (!this.submitAttempted()) {
      return [];
    }
    return this.fields.flatMap(({ id, label, state }) =>
      state()
        .errors()
        .map((error) => ({
          fieldId: id,
          label,
          message: error.message ?? `${label} is not valid.`,
        })),
    );
  });

  /** True once a submit has been attempted and something is still wrong. */
  readonly hasVisibleErrors = computed(() => this.errorSummary().length > 0);

  /** Whether a given field should currently render its error text. */
  shouldShowErrors(state: FieldState<string>): boolean {
    return (this.submitAttempted() || state.touched()) && state.errors().length > 0;
  }

  messagesFor(state: FieldState<string>, label: string): string[] {
    return state.errors().map((error) => error.message ?? `${label} is not valid.`);
  }

  async submitQuote(): Promise<void> {
    if (this.isSubmitting()) {
      return;
    }

    this.submitAttempted.set(true);
    // Clear the previous outcome so a stale "created" banner cannot sit above a new attempt.
    this.submission.set({ kind: 'editing' });

    await submit(this.form, {
      action: async () => {
        this.submission.set({ kind: 'submitting' });
        const outcome = await this.api.create({
          author: this.form.author().value().trim(),
          text: this.form.text().value().trim(),
        });

        switch (outcome.status) {
          case 'created':
            this.submission.set({ kind: 'created', quote: outcome.quote });
            this.resetForm();
            break;
          case 'rejected':
            this.submission.set({ kind: 'rejected', message: outcome.message });
            break;
          case 'unauthenticated':
            this.submission.set({ kind: 'unauthenticated' });
            break;
          case 'forbidden':
            this.submission.set({ kind: 'forbidden' });
            break;
          case 'failed':
            this.submission.set({ kind: 'failed', message: outcome.message });
            break;
        }
        return undefined;
      },
      onInvalid: () => {
        this.submission.set({ kind: 'editing' });
      },
    });
  }

  /**
   * Moves focus to the first invalid control in DOM order and returns its id.
   *
   * Returns `null` when nothing is invalid, so the caller can send focus to the server-error
   * alert instead of leaving it stranded on the submit button.
   */
  focusFirstInvalidField(): string | null {
    for (const { id, state } of this.fields) {
      const fieldState = state();
      if (fieldState.errors().length > 0) {
        fieldState.focusBoundControl();
        return id;
      }
    }
    return null;
  }

  focusField(fieldId: string): void {
    this.fields.find((field) => field.id === fieldId)?.state().focusBoundControl();
  }

  dismissOutcome(): void {
    this.submission.set({ kind: 'editing' });
  }

  private resetForm(): void {
    this.model.set({ ...EMPTY_CREATE_QUOTE });
    this.form().reset();
    this.submitAttempted.set(false);
  }
}
