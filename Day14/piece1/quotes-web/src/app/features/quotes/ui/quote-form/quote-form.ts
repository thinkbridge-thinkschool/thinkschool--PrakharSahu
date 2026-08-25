import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  output,
  viewChild,
} from '@angular/core';
import { FieldState, FormField } from '@angular/forms/signals';

import { Quote } from '../../domain/quote';
import { AUTHOR_FIELD_ID, CreateQuoteStore, TEXT_FIELD_ID } from '../../state/create-quote-store';

/** Below this many characters left, the counter starts announcing. */
const COUNTER_ANNOUNCE_THRESHOLD = 20;

/**
 * Create-a-quote form for `POST /api/quotes`.
 *
 * Accessibility decisions worth knowing before changing anything here:
 *
 * - Every input has an explicit `<label for>`; no placeholder is used as a label, because a
 *   placeholder disappears the moment the user types.
 * - `aria-describedby` is rebuilt per render and only ever names elements that are actually
 *   in the DOM. Pointing at an error paragraph that is not rendered makes a screen reader
 *   announce nothing at all, which is worse than having no association.
 * - The error summary uses buttons, not `href="#id"` fragment links. A fragment link only
 *   moves focus if the target happens to be focusable, and it also pushes history entries.
 * - The submit button stays enabled while submitting and reports `aria-disabled`. Disabling
 *   it would drop it out of the tab order mid-interaction and move the user's focus for
 *   them, and it hides the reason nothing is happening.
 */
@Component({
  selector: 'app-quote-form',
  imports: [FormField],
  templateUrl: './quote-form.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuoteForm {
  protected readonly store = inject(CreateQuoteStore);
  private readonly injector = inject(Injector);

  protected readonly authorId = AUTHOR_FIELD_ID;
  protected readonly textId = TEXT_FIELD_ID;

  /** Emitted after a 201 so the page can pull the new quote into the list. */
  readonly quoteCreated = output<Quote>();

  private readonly errorSummary = viewChild<ElementRef<HTMLElement>>('errorSummary');
  private readonly outcomeAlert = viewChild<ElementRef<HTMLElement>>('outcomeAlert');

  protected authorState(): FieldState<string> {
    return this.store.form.author();
  }

  protected textState(): FieldState<string> {
    return this.store.form.text();
  }

  protected showAuthorErrors(): boolean {
    return this.store.shouldShowErrors(this.authorState());
  }

  protected showTextErrors(): boolean {
    return this.store.shouldShowErrors(this.textState());
  }

  protected authorMessages(): string[] {
    return this.store.messagesFor(this.authorState(), 'Author');
  }

  protected textMessages(): string[] {
    return this.store.messagesFor(this.textState(), 'Quote text');
  }

  /** Error first, so a screen reader reads the problem before the hint. */
  protected authorDescribedBy(): string {
    return this.describedBy(this.authorId, this.showAuthorErrors());
  }

  protected textDescribedBy(): string {
    return this.describedBy(this.textId, this.showTextErrors());
  }

  /**
   * The counter is in `aria-describedby` so it is read when the field takes focus, but it is
   * only put in a live region near the limit. A live counter on every keystroke turns typing
   * into a stream of interruptions.
   */
  protected authorCountAnnouncement = computed(() =>
    this.announcement(this.store.authorRemaining()),
  );

  protected textCountAnnouncement = computed(() => this.announcement(this.store.textRemaining()));

  protected readonly outcomeNeedsFocus = computed(() => {
    const kind = this.store.submissionState().kind;
    return kind === 'rejected' || kind === 'unauthenticated' || kind === 'forbidden' || kind === 'failed';
  });

  /** The server's own wording for the arms that carry one. */
  protected outcomeMessage(): string {
    const state = this.store.submissionState();
    return state.kind === 'rejected' || state.kind === 'failed' ? state.message : '';
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    await this.store.submitQuote();

    // Focus the first thing the user has to fix. If validation passed and the server
    // refused, focus the alert instead so the reason is not left unannounced behind them.
    const focusedFieldId = this.store.focusFirstInvalidField();
    if (focusedFieldId !== null) {
      return;
    }

    const state = this.store.submissionState();
    if (state.kind === 'created') {
      // Success is announced by `role="status"` without stealing focus, so the user can
      // carry straight on and type the next quote. Only failures pull focus.
      this.quoteCreated.emit(state.quote);
      return;
    }

    if (this.outcomeNeedsFocus()) {
      // The alert lives inside an `@switch` arm that only became active on the line above,
      // so `outcomeAlert()` is still empty at this point. Querying it now silently
      // no-ops and focus is left behind in the field the user just left.
      afterNextRender(() => this.outcomeAlert()?.nativeElement.focus(), {
        injector: this.injector,
      });
    }
  }

  protected onSummaryItemClick(fieldId: string): void {
    this.store.focusField(fieldId);
  }

  /** Exposed for the template's `@if` on the summary heading region. */
  protected focusSummary(): void {
    this.errorSummary()?.nativeElement.focus();
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
