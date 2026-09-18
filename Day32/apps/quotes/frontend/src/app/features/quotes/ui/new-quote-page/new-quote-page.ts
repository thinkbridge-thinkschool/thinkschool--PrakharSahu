import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  viewChild,
} from '@angular/core';
import { FieldState, FormField } from '@angular/forms/signals';
import { Router, RouterLink } from '@angular/router';

import {
  AUTHOR_FIELD_ID,
  CreateQuoteStore,
  TEXT_FIELD_ID,
} from '../../state/create-quote-store';

/**
 * The create route, behind `authGuard` because `POST /api/quotes` needs a token.
 *
 * Accessibility follows Day 14: explicit labels, `aria-describedby` that only ever names
 * elements actually in the DOM, focus moved to the first error on submit, and a submit
 * button that stays in the tab order while saving.
 */
@Component({
  selector: 'app-new-quote-page',
  imports: [FormField, RouterLink],
  templateUrl: './new-quote-page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [CreateQuoteStore],
})
export class NewQuotePage {
  protected readonly store = inject(CreateQuoteStore);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);

  protected readonly authorId = AUTHOR_FIELD_ID;
  protected readonly textId = TEXT_FIELD_ID;

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

  /** Error id first, so a screen reader reads the problem before the hint. */
  protected authorDescribedBy(): string {
    return this.describedBy(this.authorId, this.showAuthorErrors());
  }

  protected textDescribedBy(): string {
    return this.describedBy(this.textId, this.showTextErrors());
  }

  protected readonly failureMessage = computed(() => {
    const state = this.store.createState();
    return state.kind === 'failed' ? state.message : null;
  });

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    const created = await this.store.save();

    if (created) {
      // Success is checked FIRST, and deliberately so. A successful save resets the form to
      // empty, which makes `required` fire again — so asking "is anything invalid?" here
      // would always say yes and swallow the navigation.
      // Straight back to the list, which refetches, so what is on screen is what
      // GET /api/quotes actually returns rather than a locally patched guess.
      await this.router.navigate(['/quotes'], { queryParams: { created: created.id } });
      return;
    }

    // Nothing was sent: focus whatever the user has to fix.
    if (this.store.focusFirstInvalidField() !== null) {
      return;
    }

    if (this.failureMessage() !== null) {
      // The alert lives in an @if that only became true on the line above, so the viewChild
      // is still empty until the next render.
      afterNextRender(() => this.outcomeAlert()?.nativeElement.focus(), {
        injector: this.injector,
      });
    }
  }

  protected onSummaryItemClick(fieldId: string): void {
    if (fieldId === this.authorId) {
      this.authorState().focusBoundControl();
    } else {
      this.textState().focusBoundControl();
    }
  }

  private describedBy(fieldId: string, hasVisibleError: boolean): string {
    const ids = hasVisibleError ? [`${fieldId}-error`] : [];
    ids.push(`${fieldId}-hint`, `${fieldId}-count`);
    return ids.join(' ');
  }
}
