import { AuthorTally, Quote, SortOrder } from './quote';

/**
 * Pure derivations over a list of quotes.
 *
 * Deliberately free of Angular: the store wraps these in `computed()`, and the unit tests
 * exercise them directly with no TestBed. Sorting and filtering rules live here so there is
 * exactly one place to change when the API contract moves.
 */

/** `createdAt` as epoch milliseconds; `NaN` when the API sends something unparsable. */
export function createdAtMillis(quote: Quote): number {
  return Date.parse(quote.createdAt);
}

/** Case-insensitive match across the two free-text fields the API exposes. */
export function matchesSearch(quote: Quote, search: string): boolean {
  const needle = search.trim().toLocaleLowerCase();
  if (needle === '') {
    return true;
  }
  return (
    quote.author.toLocaleLowerCase().includes(needle) ||
    quote.text.toLocaleLowerCase().includes(needle)
  );
}

/**
 * Filters by `search`, then orders by `createdAt`.
 *
 * Quotes whose `createdAt` will not parse sort last in both directions rather than landing
 * in an arbitrary slot — `NaN` comparisons are always false, so they need an explicit branch.
 */
export function selectVisibleQuotes(
  quotes: readonly Quote[],
  search: string,
  sortOrder: SortOrder,
): Quote[] {
  const direction = sortOrder === 'newest' ? -1 : 1;

  return quotes
    .filter((quote) => matchesSearch(quote, search))
    .slice()
    .sort((left, right) => {
      const leftMillis = createdAtMillis(left);
      const rightMillis = createdAtMillis(right);

      if (Number.isNaN(leftMillis) || Number.isNaN(rightMillis)) {
        return Number.isNaN(leftMillis) === Number.isNaN(rightMillis)
          ? left.id - right.id
          : Number.isNaN(leftMillis)
            ? 1
            : -1;
      }
      if (leftMillis !== rightMillis) {
        return direction * (leftMillis - rightMillis);
      }
      // Same timestamp: fall back to id so the order is stable across reloads.
      return direction * (left.id - right.id);
    });
}

/** Quote count per author, busiest first, ties broken alphabetically. */
export function selectAuthorTallies(quotes: readonly Quote[]): AuthorTally[] {
  const counts = new Map<string, number>();
  for (const quote of quotes) {
    counts.set(quote.author, (counts.get(quote.author) ?? 0) + 1);
  }

  return [...counts]
    .map(([author, count]): AuthorTally => ({ author, count }))
    .sort((left, right) => right.count - left.count || left.author.localeCompare(right.author));
}
