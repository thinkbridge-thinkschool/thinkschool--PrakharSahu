import { Injectable, computed, effect, inject, signal } from '@angular/core';

import { describeHttpFailure } from '../../../core/http/http-failure';
import { SessionPreferences } from '../../../core/storage/session-preferences';
import { QuotesApiClient } from '../data-access/quotes-api.client';
import { QuotesFeed } from '../data-access/quotes-feed';
import { SortOrder } from '../domain/quote';
import { selectAuthorTallies, selectVisibleQuotes } from '../domain/quote-selectors';

/** Which of the mutually exclusive list states the page should render. */
export type QuotesViewStatus = 'loading' | 'failed' | 'empty' | 'no-matches' | 'ready';

interface QuotePreferences {
  readonly search: string;
  readonly sortOrder: SortOrder;
}

const PREFERENCES_KEY = 'quotes-web.list-preferences';
const DEFAULT_PREFERENCES: QuotePreferences = { search: '', sortOrder: 'newest' };

/**
 * Reactive state for the quotes page.
 *
 * Two things are *written*: `search` and `sortOrder`. Everything else is `computed()` from
 * them plus the feed, so there is no way for the list, the counts and the empty-state to
 * disagree with each other. The only `effect()` here writes to `sessionStorage` — deriving
 * state inside an effect would reintroduce exactly the drift signals are meant to remove.
 */
@Injectable({ providedIn: 'root' })
export class QuotesStore {
  private readonly feed: QuotesFeed = inject(QuotesApiClient);
  private readonly preferences = inject(SessionPreferences);
  private readonly restored = this.readPreferences();

  /** Free text matched against `author` and `text`. */
  readonly search = signal(this.restored.search);

  /** Ordering applied to `createdAt`. */
  readonly sortOrder = signal<SortOrder>(this.restored.sortOrder);

  /** The list the template renders: filtered by `search`, ordered by `sortOrder`. */
  readonly visibleQuotes = computed(() =>
    selectVisibleQuotes(this.feed.quotes(), this.search(), this.sortOrder()),
  );

  /** Quote count per author across the currently visible quotes. */
  readonly authorTallies = computed(() => selectAuthorTallies(this.visibleQuotes()));

  readonly totalCount = computed(() => this.feed.quotes().length);
  readonly visibleCount = computed(() => this.visibleQuotes().length);
  readonly hasActiveSearch = computed(() => this.search().trim() !== '');

  /** A user-facing sentence when the last load failed, otherwise `null`. */
  readonly failureMessage = computed(() => {
    const failure = this.feed.failure();
    return failure ? describeHttpFailure(failure) : null;
  });

  /** True only while refreshing on top of quotes already on screen. */
  readonly isRefreshing = computed(() => this.feed.isLoading() && this.totalCount() > 0);

  readonly status = computed<QuotesViewStatus>(() => {
    if (this.failureMessage() !== null) {
      return 'failed';
    }
    if (this.feed.isLoading() && this.totalCount() === 0) {
      return 'loading';
    }
    if (this.totalCount() === 0) {
      return 'empty';
    }
    return this.visibleCount() === 0 ? 'no-matches' : 'ready';
  });

  constructor() {
    effect(() => {
      const snapshot: QuotePreferences = { search: this.search(), sortOrder: this.sortOrder() };
      this.preferences.write(PREFERENCES_KEY, snapshot);
    });
  }

  refresh(): void {
    this.feed.refresh();
  }

  clearSearch(): void {
    this.search.set('');
  }

  /** Restored preferences are untrusted input — narrow them before they reach a signal. */
  private readPreferences(): QuotePreferences {
    const stored = this.preferences.read<Partial<QuotePreferences>>(
      PREFERENCES_KEY,
      DEFAULT_PREFERENCES,
    );

    return {
      search: typeof stored?.search === 'string' ? stored.search : DEFAULT_PREFERENCES.search,
      sortOrder:
        stored?.sortOrder === 'newest' || stored?.sortOrder === 'oldest'
          ? stored.sortOrder
          : DEFAULT_PREFERENCES.sortOrder,
    };
  }
}
