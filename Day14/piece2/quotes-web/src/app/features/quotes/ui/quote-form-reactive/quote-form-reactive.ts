import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
  output,
} from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';

import { Quote } from '../../domain/quote';
import {
  CreateQuoteReactiveStore,
  REACTIVE_AUTHOR_FIELD_ID,
  REACTIVE_TEXT_FIELD_ID,
} from '../../state/create-quote-reactive-store';
import { QuoteSubmission } from '../../state/quote-submission';

const COUNTER_ANNOUNCE_THRESHOLD = 20;

/**
 * The reactive-forms build of the same create-quote form.
 *
 * Same markup, same messages, same accessibility contract as `QuoteForm`; only the form
 * mechanics differ. Kept side by side so the comparison in exercise.txt can point at two
 * real implementations rather than describing one from memory.
 */
@Component({
  selector: 'app-quote-form-reactive',
  imports: [ReactiveFormsModule],
  templateUrl: './quote-form-reactive.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [CreateQuoteReactiveStore, QuoteSubmission],
})
export class QuoteFormReactive {
  protected readonly store = inject(CreateQuoteReactiveStore);
  private readonly injector = inject(Injector);

  protected readonly authorId = REACTIVE_AUTHOR_FIELD_ID;
  protected readonly textId = REACTIVE_TEXT_FIELD_ID;

  readonly quoteCreated = output<Quote>();

  private readonly host = inject(ElementRef);

  protected authorControl(): FormControl<string> {
    return this.store.form.controls.author;
  }

  protected textControl(): FormControl<string> {
    return this.store.form.controls.text;
  }

  protected showAuthorErrors(): boolean {
    return this.store.shouldShowErrors(this.authorControl());
  }

  protected showTextErrors(): boolean {
    return this.store.shouldShowErrors(this.textControl());
  }

  protected authorMessages(): string[] {
    return this.store.messagesFor(this.authorControl(), 'Author');
  }

  protected textMessages(): string[] {
    return this.store.messagesFor(this.textControl(), 'Quote text');
  }

  protected authorDescribedBy(): string {
    return this.describedBy(this.authorId, this.showAuthorErrors());
  }

  protected textDescribedBy(): string {
    return this.describedBy(this.textId, this.showTextErrors());
  }

  protected authorCountAnnouncement(): string {
    return this.announcement(this.store.authorRemaining());
  }

  protected textCountAnnouncement(): string {
    return this.announcement(this.store.textRemaining());
  }

  protected outcomeMessage(): string {
    const state = this.store.submissionState();
    return state.kind === 'rejected' || state.kind === 'failed' ? state.message : '';
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    await this.store.submitQuote();

    const invalidFieldId = this.store.firstInvalidFieldId();
    if (invalidFieldId !== null) {
      this.focusById(invalidFieldId);
      return;
    }

    const state = this.store.submissionState();
    if (state.kind === 'created') {
      this.quoteCreated.emit(state.quote);
      return;
    }

    if (this.store.outcomeNeedsFocus()) {
      afterNextRender(
        () => {
          const alert = (this.host.nativeElement as HTMLElement).querySelector<HTMLElement>(
            '[data-outcome-alert]',
          );
          alert?.focus();
        },
        { injector: this.injector },
      );
    }
  }

  protected onSummaryItemClick(fieldId: string): void {
    this.focusById(fieldId);
  }

  /** Scoped to this component's host so two forms on a page cannot steal each other's focus. */
  private focusById(fieldId: string): void {
    (this.host.nativeElement as HTMLElement)
      .querySelector<HTMLElement>(`#${fieldId}`)
      ?.focus();
  }

  private describedBy(fieldId: string, hasVisibleError: boolean): string {
    const ids = hasVisibleError ? [`${fieldId}-error`] : [];
    ids.push(`${fieldId}-hint`, `${fieldId}-count`);
    return ids.join(' ');
  }

  private announcement(remaining: number): string {
    if (remaining < 0) {
      return `${Math.abs(remaining)} characters over the limit.`;
    }
    return remaining <= COUNTER_ANNOUNCE_THRESHOLD ? `${remaining} characters remaining.` : '';
  }
}
