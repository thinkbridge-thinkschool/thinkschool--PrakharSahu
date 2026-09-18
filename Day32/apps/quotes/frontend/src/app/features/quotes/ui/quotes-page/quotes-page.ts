import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input } from '@angular/core';
import { Router, RouterLink } from '@angular/router';

import { QuotesStore } from '../../state/quotes-store';

/**
 * The list route, lazy-loaded as its own chunk.
 *
 * The filter lives in the URL (`/quotes?q=mentor`), not in a local signal. That makes it
 * shareable, survivable across a reload, and part of the history — and it is why the detail
 * link preserves query params: coming back from a quote returns you to the filtered list you
 * left, not to the top of an unfiltered one.
 */
@Component({
  selector: 'app-quotes-page',
  imports: [DatePipe, RouterLink],
  templateUrl: './quotes-page.html',
  styleUrl: './quotes-page.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuotesPage implements OnInit {
  protected readonly store = inject(QuotesStore);
  private readonly router = inject(Router);

  /**
   * Bound from `?q=` by `withComponentInputBinding()`.
   *
   * The transform is not decoration. When the query parameter is absent the router binds
   * `undefined`, which OVERRIDES the declared default of `''` — so `q()` is undefined on a
   * plain `/quotes` and `q().trim()` throws. Coalescing in a transform is the only place
   * that fixes it for every reader at once.
   */
  readonly q = input('', { transform: (value: string | undefined) => value ?? '' });

  protected readonly search = computed(() => this.q().trim().toLocaleLowerCase());

  protected readonly visible = computed(() => {
    const needle = this.search();
    const all = this.store.quotes();
    if (needle === '') {
      return all;
    }
    return all.filter(
      (quote) =>
        quote.author.toLocaleLowerCase().includes(needle) ||
        quote.text.toLocaleLowerCase().includes(needle),
    );
  });

  protected readonly filteredOut = computed(
    () => this.store.viewStatus() === 'ready' && this.visible().length === 0,
  );

  /**
   * One load per component instance.
   *
   * Deliberately `ngOnInit` and not `effect()`: `load()` reads the store's state signal on
   * its way to writing it, so calling it from an effect makes the effect depend on the very
   * signal it updates — an infinite reload loop. A lifecycle hook is the right tool for a
   * one-shot; effects are for keeping something in sync, and nothing here needs syncing.
   *
   * The filter is applied client-side because `GET /api/quotes` takes no query parameters
   * (pinned by Day 15's contract test).
   */
  ngOnInit(): void {
    void this.store.load();
  }

  /** Writes the box straight into the URL, replacing history so typing leaves one entry. */
  protected onSearch(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    void this.router.navigate([], {
      queryParams: { q: value === '' ? null : value },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
}
