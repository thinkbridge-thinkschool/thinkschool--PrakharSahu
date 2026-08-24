import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';

import { QuoteDetailStore } from '../../state/quote-detail-store';
import { QuotesStore } from '../../state/quotes-store';
import { QuoteDetail } from '../quote-detail/quote-detail';
import { QuoteFilters } from '../quote-filters/quote-filters';
import { QuoteList } from '../quote-list/quote-list';

/**
 * The one component in the feature that knows the stores exist. Its children take plain
 * inputs, which keeps them trivially testable and keeps the data flow one-directional.
 *
 * List and detail are separate stores over separate endpoints; the only thing joining them
 * is `QuoteSelection`, which the list writes and the detail follows.
 */
@Component({
  selector: 'app-quotes-page',
  imports: [QuoteFilters, QuoteList, QuoteDetail],
  templateUrl: './quotes-page.html',
  styleUrl: './quotes-page.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuotesPage {
  protected readonly list = inject(QuotesStore);
  protected readonly detail = inject(QuoteDetailStore);

  /** Distinguishes "the API has nothing" from "your filter excluded everything". */
  protected readonly emptyMessage = computed(() =>
    this.list.status() === 'empty'
      ? 'The API returned no quotes. Create one with POST /api/quotes, then refresh.'
      : `No quote matches “${this.list.search().trim()}”.`,
  );
}
