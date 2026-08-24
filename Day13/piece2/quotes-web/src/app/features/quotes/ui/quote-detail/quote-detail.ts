import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { Quote } from '../../domain/quote';
import { QuoteDetailStatus } from '../../state/quote-detail-store';

/**
 * Presentational detail pane. Every state it can show is reachable from its inputs, so a
 * test can put it in "not found" without an HTTP stack anywhere in sight.
 */
@Component({
  selector: 'app-quote-detail',
  imports: [DatePipe],
  templateUrl: './quote-detail.html',
  styleUrl: './quote-detail.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuoteDetail {
  readonly status = input.required<QuoteDetailStatus>();
  readonly quote = input<Quote | undefined>(undefined);
  readonly failureMessage = input<string | null>(null);
  /** Shown in the not-found copy so the user knows which id is missing. */
  readonly quoteId = input<number | null>(null);

  readonly retryRequested = output<void>();
  readonly closeRequested = output<void>();
}
