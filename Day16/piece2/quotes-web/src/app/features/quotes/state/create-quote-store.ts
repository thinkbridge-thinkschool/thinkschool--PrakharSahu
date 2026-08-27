import { Injectable, computed, inject, signal } from '@angular/core';
import { FieldState, ValidationError, form, required, schema, submit, validate } from '@angular/forms/signals';

import { ApiError } from '../../../core/http/api-error';
import { QuotesApiClient } from '../data-access/quotes-api.client';
import {
  QUOTE_AUTHOR_MAX_LENGTH,
  QUOTE_TEXT_MAX_LENGTH,
  isBlank,
  serverLengthOf,
} from '../domain/create-quote';
import { Quote } from '../domain/quote';

export const AUTHOR_FIELD_ID = 'create-quote-author';
export const TEXT_FIELD_ID = 'create-quote-text';

export interface CreateQuoteModel {
  author: string;
  text: string;
}

const EMPTY: CreateQuoteModel = { author: '', text: '' };

export type CreateState =
  | { readonly kind: 'editing' }
  | { readonly kind: 'saving' }
  | { readonly kind: 'created'; readonly quote: Quote }
  | { readonly kind: 'failed'; readonly message: string };

/** `''` is `required()`'s job; only catch whitespace-only, or the same error appears twice. */
function notWhitespaceOnly(message: string) {
  return ({ value }: { value: () => string }): ValidationError | undefined =>
    value() !== '' && isBlank(value()) ? { kind: 'blank', message } : undefined;
}

/**
 * Length checked by hand rather than with `maxLength()`.
 *
 * Signal Forms' `maxLength()` also stamps a native `maxlength` attribute, and the browser
 * then truncates over-long input silently — no error, nothing announced, and the validator
 * unreachable. Day 14 found that the hard way; this keeps the attribute off so the limit is
 * reported instead of enforced by deletion.
 */
function withinLength(limit: number) {
  return ({ value }: { value: () => string }): ValidationError | undefined => {
    const over = serverLengthOf(value()) - limit;
    return over > 0
      ? { kind: 'maxlength', message: `Must be ${limit} characters or fewer. Remove ${over}.` }
      : undefined;
  };
}

export const createQuoteSchema = schema<CreateQuoteModel>((path) => {
  required(path.author, { message: 'Enter an author to credit.' });
  validate(path.author, notWhitespaceOnly('Enter an author to credit.'));
  validate(path.author, withinLength(QUOTE_AUTHOR_MAX_LENGTH));

  required(path.text, { message: 'Enter the quote text.' });
  validate(path.text, notWhitespaceOnly('Enter the quote text.'));
  validate(path.text, withinLength(QUOTE_TEXT_MAX_LENGTH));
});

/**
 * State for the create form. Provided by the page, not in root — a half-typed quote is view
 * state and should die with the view.
 */
@Injectable()
export class CreateQuoteStore {
  private readonly api = inject(QuotesApiClient);

  private readonly model = signal<CreateQuoteModel>({ ...EMPTY });
  readonly form = form(this.model, createQuoteSchema);

  private readonly submitAttempted = signal(false);
  private readonly state = signal<CreateState>({ kind: 'editing' });

  readonly createState = this.state.asReadonly();
  readonly isSaving = computed(() => this.state().kind === 'saving');

  readonly authorMaxLength = QUOTE_AUTHOR_MAX_LENGTH;
  readonly textMaxLength = QUOTE_TEXT_MAX_LENGTH;
  readonly authorRemaining = computed(
    () => QUOTE_AUTHOR_MAX_LENGTH - serverLengthOf(this.form.author().value()),
  );
  readonly textRemaining = computed(
    () => QUOTE_TEXT_MAX_LENGTH - serverLengthOf(this.form.text().value()),
  );

  private readonly fields: readonly { id: string; label: string; state: () => FieldState<string> }[] =
    [
      { id: AUTHOR_FIELD_ID, label: 'Author', state: () => this.form.author() },
      { id: TEXT_FIELD_ID, label: 'Quote text', state: () => this.form.text() },
    ];

  readonly errorSummary = computed(() => {
    if (!this.submitAttempted()) {
      return [];
    }
    return this.fields.flatMap(({ id, label, state }) =>
      state()
        .errors()
        .map((error) => ({ fieldId: id, label, message: error.message ?? `${label} is not valid.` })),
    );
  });

  readonly hasVisibleErrors = computed(() => this.errorSummary().length > 0);

  shouldShowErrors(state: FieldState<string>): boolean {
    return (this.submitAttempted() || state.touched()) && state.errors().length > 0;
  }

  messagesFor(state: FieldState<string>, label: string): string[] {
    return state.errors().map((error) => error.message ?? `${label} is not valid.`);
  }

  /** Resolves with the created quote, or null if nothing was sent. */
  async save(): Promise<Quote | null> {
    if (this.isSaving()) {
      return null;
    }
    this.submitAttempted.set(true);
    this.state.set({ kind: 'editing' });

    let created: Quote | null = null;

    await submit(this.form, {
      action: async () => {
        this.state.set({ kind: 'saving' });
        try {
          created = await this.api.createQuote({
            author: this.form.author().value().trim(),
            text: this.form.text().value().trim(),
          });
          this.state.set({ kind: 'created', quote: created });
          this.reset();
        } catch (failure) {
          this.state.set({ kind: 'failed', message: this.explain(failure) });
        }
        return undefined;
      },
      onInvalid: () => this.state.set({ kind: 'editing' }),
    });

    return created;
  }

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

  dismiss(): void {
    this.state.set({ kind: 'editing' });
  }

  /**
   * `FieldTree.reset()` clears touched and dirty but explicitly does NOT change the data
   * model, so the value has to be put back by hand. Day 14 verified this directly.
   */
  private reset(): void {
    this.model.set({ ...EMPTY });
    this.form().reset();
    this.submitAttempted.set(false);
  }

  private explain(failure: unknown): string {
    if (!(failure instanceof ApiError)) {
      return 'The quote could not be saved.';
    }
    switch (failure.kind) {
      case 'unauthorized':
        return 'Your session expired. Sign in and try again.';
      case 'forbidden':
        return 'Your token does not carry the quotes.write scope, so the API refused the write.';
      default:
        // A 400 arrives as the server's own DomainError sentence, e.g.
        // "Text must be between 1 and 1000 characters."
        return failure.friendlyMessage;
    }
  }
}
