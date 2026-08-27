import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';

import { AccessTokenStore } from '../../../../core/auth/access-token.store';

/**
 * The one route that genuinely needs a token: it targets `POST /api/quotes`, which is
 * guarded by `RequireAuthorization("can-edit-quotes")`.
 *
 * Reaching this component at all means `authGuard` let the navigation through. The form
 * itself is Day 14's subject and is deliberately not rebuilt here — this piece is about
 * routing, and the page exists so the guard has something real to protect.
 */
@Component({
  selector: 'app-new-quote-page',
  imports: [RouterLink],
  templateUrl: './new-quote-page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NewQuotePage {
  protected readonly tokens = inject(AccessTokenStore);
}
