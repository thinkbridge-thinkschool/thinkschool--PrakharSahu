import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { Quote } from '../../domain/quote';

/**
 * Presentational list of quotes. Takes data in, emits the chosen id out, injects nothing —
 * every state it can be in is reachable from its inputs alone.
 */
@Component({
  selector: 'app-quote-list',
  imports: [DatePipe],
  templateUrl: './quote-list.html',
  styleUrl: './quote-list.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuoteList {
  readonly quotes = input.required<readonly Quote[]>();

  /** Rendered in place of the list when `quotes` is empty. */
  readonly emptyMessage = input('No quotes to show.');

  /** Highlights the open row; `null` when the detail pane is closed. */
  readonly selectedQuoteId = input<number | null>(null);

  readonly quoteSelected = output<number>();
}
