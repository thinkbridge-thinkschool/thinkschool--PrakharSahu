import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';

import { QuotesStore } from '../../state/quotes-store';
import { QuoteFilters } from '../quote-filters/quote-filters';
import { QuoteList } from '../quote-list/quote-list';

/**
 * The one component in the feature that knows the store exists. Its children take plain
 * inputs, which keeps them trivially testable and keeps the data flow one-directional.
 */
@Component({
  selector: 'app-quotes-page',
  imports: [QuoteFilters, QuoteList],
  templateUrl: './quotes-page.html',
  styleUrl: './quotes-page.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuotesPage {
  protected readonly store = inject(QuotesStore);

  /** Distinguishes "the API has nothing" from "your filter excluded everything". */
  protected readonly emptyMessage = computed(() =>
    this.store.status() === 'empty'
      ? 'The API returned no quotes. Create one with POST /api/quotes, then refresh.'
      : `No quote matches “${this.store.search().trim()}”.`,
  );
}
