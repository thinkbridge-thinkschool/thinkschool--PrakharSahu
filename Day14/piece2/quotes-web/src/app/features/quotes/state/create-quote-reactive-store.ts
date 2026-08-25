import { Injectable, computed, inject, signal } from '@angular/core';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  NonNullableFormBuilder,
  ValidationErrors,
  ValidatorFn,
  Validators,
} from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';

import {
  QUOTE_AUTHOR_MAX_LENGTH,
  QUOTE_TEXT_MAX_LENGTH,
  isBlank,
  serverLengthOf,
} from '../domain/create-quote';
import { FieldErrorSummaryItem } from './create-quote-store';
import { QuoteSubmission } from './quote-submission';

export const REACTIVE_AUTHOR_FIELD_ID = 'reactive-quote-author';
export const REACTIVE_TEXT_FIELD_ID = 'reactive-quote-text';

/** `"   "` is non-empty but the server rejects it via `string.IsNullOrWhiteSpace`. */
const notWhitespaceOnly: ValidatorFn = (control: AbstractControl): ValidationErrors | null => {
  const value = String(control.value ?? '');
  return value !== '' && isBlank(value) ? { blank: true } : null;
};

/**
 * Length rule written by hand so the message can say how much to remove.
 *
 * Unlike Signal Forms' `maxLength()`, Angular's `Validators.maxLength` does NOT put a
 * native `maxlength` attribute on the control — only the `[maxlength]` *directive* does
 * that. So the reactive version never had the silent-truncation problem from piece 1.
 * It is written out here anyway to keep the two messages identical.
 */
function withinLength(limit: number): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const over = serverLengthOf(String(control.value ?? '')) - limit;
    return over > 0 ? { maxlength: { limit, over } } : null;
  };
}

interface CreateQuoteFormControls {
  author: FormControl<string>;
  text: FormControl<string>;
}

/**
 * The same form again, described with `FormBuilder`.
 *
 * Written to be compared with `CreateQuoteStore`, not to be better: identical fields,
 * identical rules, identical messages, identical a11y surface. Both delegate the actual
 * POST to `QuoteSubmission`, so nothing below is about talking to the API.
 */
@Injectable()
export class CreateQuoteReactiveStore {
  private readonly builder = inject(NonNullableFormBuilder);
  private readonly submission = inject(QuoteSubmission);

  readonly form: FormGroup<CreateQuoteFormControls> = this.builder.group({
    author: this.builder.control('', [
      Validators.required,
      notWhitespaceOnly,
      withinLength(QUOTE_AUTHOR_MAX_LENGTH),
    ]),
    text: this.builder.control('', [
      Validators.required,
      notWhitespaceOnly,
      withinLength(QUOTE_TEXT_MAX_LENGTH),
    ]),
  });

  /**
   * Reactive forms report state through observables, so everything the template reads has
   * to be bridged into a signal. Without this the component would need `| async` or manual
   * change detection, because a `FormGroup` mutating internally is invisible to a zoneless
   * app. This bridge is the single biggest structural difference between the two versions.
   */
  private readonly statusTick = toSignal(this.form.events, { initialValue: null });

  private readonly submitAttempted = signal(false);
  readonly hasAttemptedSubmit = this.submitAttempted.asReadonly();

  readonly submissionState = this.submission.submissionState;
  readonly isSubmitting = this.submission.isSubmitting;
  readonly outcomeNeedsFocus = this.submission.needsFocus;

  readonly authorMaxLength = QUOTE_AUTHOR_MAX_LENGTH;
  readonly textMaxLength = QUOTE_TEXT_MAX_LENGTH;

  readonly authorRemaining = computed(() => {
    this.statusTick();
    return QUOTE_AUTHOR_MAX_LENGTH - serverLengthOf(this.form.controls.author.value);
  });
  readonly textRemaining = computed(() => {
    this.statusTick();
    return QUOTE_TEXT_MAX_LENGTH - serverLengthOf(this.form.controls.text.value);
  });

  /** Reactive forms expose `pristine` directly; Signal Forms only has `dirty`. */
  readonly isPristine = computed(() => {
    this.statusTick();
    return this.form.pristine;
  });
  readonly isDirty = computed(() => {
    this.statusTick();
    return this.form.dirty;
  });
  readonly isTouched = computed(() => {
    this.statusTick();
    return this.form.touched;
  });
  readonly isValid = computed(() => {
    this.statusTick();
    return this.form.valid;
  });

  private readonly fields: readonly {
    id: string;
    label: string;
    control: () => FormControl<string>;
  }[] = [
    { id: REACTIVE_AUTHOR_FIELD_ID, label: 'Author', control: () => this.form.controls.author },
    { id: REACTIVE_TEXT_FIELD_ID, label: 'Quote text', control: () => this.form.controls.text },
  ];

  readonly errorSummary = computed<FieldErrorSummaryItem[]>(() => {
    this.statusTick();
    if (!this.submitAttempted()) {
      return [];
    }
    return this.fields.flatMap(({ id, label, control }) =>
      this.messagesFor(control(), label).map((message) => ({ fieldId: id, label, message })),
    );
  });

  readonly hasVisibleErrors = computed(() => this.errorSummary().length > 0);

  shouldShowErrors(control: FormControl<string>): boolean {
    this.statusTick();
    return (this.submitAttempted() || control.touched) && control.invalid;
  }

  /**
   * Reactive forms hand back an untyped error bag, so every message is rebuilt here from
   * error keys. Signal Forms carries the message with the error, which is why its store
   * has no equivalent of this method.
   */
  messagesFor(control: FormControl<string>, label: string): string[] {
    const errors = control.errors;
    if (!errors) {
      return [];
    }

    const messages: string[] = [];
    const emptyMessage =
      label === 'Author' ? 'Enter an author to credit.' : 'Enter the quote text.';

    if (errors['required'] || errors['blank']) {
      messages.push(emptyMessage);
    }
    const tooLong = errors['maxlength'] as { limit: number; over: number } | undefined;
    if (tooLong) {
      messages.push(`Must be ${tooLong.limit} characters or fewer. Remove ${tooLong.over}.`);
    }
    // No 'server' key is handled here on purpose. `POST /api/quotes` returns a single
    // unattributed DomainError — `{"message":"Text must be ..."}` with no field name — so
    // there is nothing to attach it to without parsing English. It goes in the banner
    // instead, in both implementations.
    return messages;
  }

  async submitQuote(): Promise<void> {
    if (this.isSubmitting()) {
      return;
    }

    this.submitAttempted.set(true);
    this.submission.clear();

    // No `submit()` helper here: marking everything touched is the caller's job.
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      return;
    }

    const outcome = await this.submission.send({
      author: this.form.controls.author.value.trim(),
      text: this.form.controls.text.value.trim(),
    });

    if (outcome.kind === 'created') {
      this.resetForm();
    }
  }

  /**
   * Reports which control to focus rather than focusing it.
   *
   * A `FormControl` has no handle on its DOM element, so there is no reactive-forms
   * equivalent of Signal Forms' `focusBoundControl()`. The choice is to reach into the
   * document from the store — which puts DOM access in the state layer — or to hand the id
   * back and let the component, which legitimately owns the DOM, do the focusing. This
   * takes the second option; the Signal Forms store needs neither.
   */
  firstInvalidFieldId(): string | null {
    return this.fields.find(({ control }) => control().invalid)?.id ?? null;
  }

  dismissOutcome(): void {
    this.submission.clear();
  }

  /** One call clears value, touched, dirty and validity — no separate model to put back. */
  private resetForm(): void {
    this.form.reset({ author: '', text: '' });
    this.submitAttempted.set(false);
  }
}
