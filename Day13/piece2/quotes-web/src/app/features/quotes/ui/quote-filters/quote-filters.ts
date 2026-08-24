import { ChangeDetectionStrategy, Component, input, model, output } from '@angular/core';

import { SortOrder } from '../../domain/quote';

/**
 * Search box and sort selector.
 *
 * `search` and `sortOrder` are `model()` signals, so the page binds its own store signals
 * straight through with `[(search)]` — no change handlers threaded down, and no second copy
 * of the filter state living inside this component.
 */
@Component({
  selector: 'app-quote-filters',
  templateUrl: './quote-filters.html',
  styleUrl: './quote-filters.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuoteFilters {
  readonly search = model.required<string>();
  readonly sortOrder = model.required<SortOrder>();

  readonly visibleCount = input.required<number>();
  readonly totalCount = input.required<number>();
  readonly isRefreshing = input(false);

  readonly refreshRequested = output<void>();

  protected onSearchInput(event: Event): void {
    this.search.set((event.target as HTMLInputElement).value);
  }

  protected onSortOrderChange(event: Event): void {
    this.sortOrder.set((event.target as HTMLSelectElement).value as SortOrder);
  }
}
