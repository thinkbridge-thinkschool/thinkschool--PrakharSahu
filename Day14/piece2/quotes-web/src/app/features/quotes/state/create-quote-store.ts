import { Injectable, computed, inject, signal } from '@angular/core';
import { FieldState, form, submit } from '@angular/forms/signals';

import { QUOTE_AUTHOR_MAX_LENGTH, QUOTE_TEXT_MAX_LENGTH } from '../domain/create-quote';
import {
  CreateQuoteModel,
  EMPTY_CREATE_QUOTE,
  createQuoteSchema,
  remainingCharacters,
} from './create-quote.schema';
import { QuoteSubmission } from './quote-submission';

/** Stable ids so the label, the input, its errors and the summary link all agree. */
export const AUTHOR_FIELD_ID = 'create-quote-author';
export const TEXT_FIELD_ID = 'create-quote-text';

/** One row of the error summary: enough to render a control that focuses the bad input. */
export interface FieldErrorSummaryItem {
  readonly fieldId: string;
  readonly label: string;
  readonly message: string;
}

/**
 * The Signal Forms implementation.
 *
 * Compare with `CreateQuoteReactiveStore`, which describes the same form with
 * `FormBuilder`. Everything after "the form is valid" is delegated to `QuoteSubmission`,
 * so the two files differ only where the two APIs genuinely differ.
 *
 * Provided by the form component rather than in root: a form's state is view state, and
 * two instances on one page must not share it.
 */
@Injectable()
export class CreateQuoteStore {
  private readonly submission = inject(QuoteSubmission);

  private readonly model = signal<CreateQuoteModel>({ ...EMPTY_CREATE_QUOTE });

  readonly form = form(this.model, createQuoteSchema);

  /**
   * Errors stay hidden until a field is left or a submit is attempted. Signal Forms tracks
   * `touched()` and `dirty()` per field but has no `pristine` — it is simply `!dirty()`.
   */
  private readonly submitAttempted = signal(false);
  readonly hasAttemptedSubmit = this.submitAttempted.asReadonly();

  readonly submissionState = this.submission.submissionState;
  readonly isSubmitting = this.submission.isSubmitting;
  readonly outcomeNeedsFocus = this.submission.needsFocus;

  readonly authorMaxLength = QUOTE_AUTHOR_MAX_LENGTH;
  readonly textMaxLength = QUOTE_TEXT_MAX_LENGTH;

  readonly authorRemaining = computed(() =>
    remainingCharacters(this.form.author().value(), QUOTE_AUTHOR_MAX_LENGTH),
  );
  readonly textRemaining = computed(() =>
    remainingCharacters(this.form.text().value(), QUOTE_TEXT_MAX_LENGTH),
  );

  /** Whole-form flags, for the state read-out the verification log exercises. */
  readonly isDirty = computed(() => this.form.author().dirty() || this.form.text().dirty());
  readonly isPristine = computed(() => !this.isDirty());
  readonly isTouched = computed(() => this.form.author().touched() || this.form.text().touched());
  readonly isValid = computed(() => this.form().valid());

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

  async submitQuote(): Promise<void> {
    if (this.isSubmitting()) {
      return;
    }

    this.submitAttempted.set(true);
    this.submission.clear();

    await submit(this.form, {
      action: async () => {
        const outcome = await this.submission.send({
          author: this.form.author().value().trim(),
          text: this.form.text().value().trim(),
        });
        if (outcome.kind === 'created') {
          this.resetForm();
        }
        return undefined;
      },
      onInvalid: () => this.submission.clear(),
    });
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

  focusField(fieldId: string): void {
    this.fields.find((field) => field.id === fieldId)?.state().focusBoundControl();
  }

  dismissOutcome(): void {
    this.submission.clear();
  }

  /**
   * `FieldTree.reset()` clears touched and dirty but explicitly does NOT change the data
   * model — see its doc comment in @angular/forms. The value has to be put back by hand,
   * which is the one place `FormGroup.reset()` is plainly less surprising.
   */
  private resetForm(): void {
    this.model.set({ ...EMPTY_CREATE_QUOTE });
    this.form().reset();
    this.submitAttempted.set(false);
  }
}
