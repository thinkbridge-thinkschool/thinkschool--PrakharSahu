import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { DatePipe, KeyValuePipe } from '@angular/common';

import { AccessTokenStore } from './core/auth/access-token.store';
import { QuotesStore } from './features/quotes/state/quotes-store';

/**
 * A deliberately thin page. Day 15 is about the HTTP layer, so the UI exists only to
 * exercise it: load the list, force a 404, and show that whatever comes back is a sentence
 * a person can read rather than a status code or a stack trace.
 */
@Component({
  selector: 'app-root',
  imports: [DatePipe, KeyValuePipe],
  templateUrl: './app.html',
  styleUrl: './app.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  protected readonly store = inject(QuotesStore);
  protected readonly tokens = inject(AccessTokenStore);
}
