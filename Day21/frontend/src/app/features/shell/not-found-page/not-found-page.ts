import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';

/**
 * Where unmatched urls land — including `/quotes/abc`, which never matches the detail route
 * because `quoteIdMustBeInteger` rejects it at `canMatch` time.
 *
 * The wildcard renders this component in place rather than redirecting, so the address the
 * user actually typed survives. A `redirectTo` would rewrite the url and hide the typo they
 * need to see.
 */
@Component({
  selector: 'app-not-found-page',
  imports: [RouterLink],
  template: `
    <section data-testid="not-found" class="not-found">
      <p class="not-found-code" aria-hidden="true">404</p>
      <h2>That page does not exist</h2>
      <p class="muted">
        Nothing matches <code data-testid="attempted-url">{{ attemptedUrl }}</code
        >. Quote ids are whole numbers — the API's route is
        <code>/api/quotes/&#123;id:int&#125;</code>.
      </p>
      <p class="not-found-actions"><a routerLink="/quotes">← Back to all quotes</a></p>
    </section>
  `,
  styles: `
    .not-found {
      display: grid;
      gap: 0.6rem;
      justify-items: start;
      padding: 2.5rem 0;
    }
    .not-found-code {
      margin: 0;
      font-size: 3rem;
      font-weight: 700;
      line-height: 1;
      letter-spacing: -0.03em;
      color: var(--accent);
      opacity: 0.35;
    }
    .not-found-actions {
      margin: 0.5rem 0 0;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NotFoundPage {
  /** Read once: this page never re-renders for a different url, it is replaced. */
  protected readonly attemptedUrl = inject(Router).url;
}
