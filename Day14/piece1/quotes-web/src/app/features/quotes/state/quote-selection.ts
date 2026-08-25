import { Injectable, signal } from '@angular/core';

/**
 * Which quote the detail pane is showing.
 *
 * Its own service rather than a field on either store: the list writes it, the detail
 * reads it, and neither has to know the other exists. `null` means nothing is selected,
 * which is a real state — not a stand-in for "loading".
 */
@Injectable({ providedIn: 'root' })
export class QuoteSelection {
  private readonly selected = signal<number | null>(null);

  readonly selectedQuoteId = this.selected.asReadonly();

  select(quoteId: number): void {
    this.selected.set(quoteId);
  }

  clear(): void {
    this.selected.set(null);
  }
}
