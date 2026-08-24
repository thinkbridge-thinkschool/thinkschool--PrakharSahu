import { Quote } from './quote';
import { createdAtMillis, matchesSearch, selectAuthorTallies, selectVisibleQuotes } from './quote-selectors';

/**
 * Fixtures use the real wire shape of `GET /api/quotes` — all six fields, and the
 * `+00:00` offset form the API actually emits — with synthetic authors.
 */
function quoteFixture(overrides: Partial<Quote> & Pick<Quote, 'id'>): Quote {
  return {
    text: 'Prefer CTEs to correlated subqueries.',
    author: 'Ada Lovelace',
    createdAt: '2026-08-10T10:00:00+00:00',
    isDeleted: false,
    userId: 1,
    ...overrides,
  };
}

describe('createdAtMillis', () => {
  it('parses the offset form the API sends', () => {
    expect(createdAtMillis(quoteFixture({ id: 1, createdAt: '2026-08-10T10:00:00+00:00' }))).toBe(
      Date.UTC(2026, 7, 10, 10, 0, 0),
    );
  });

  it('returns NaN rather than throwing on an unparsable value', () => {
    expect(createdAtMillis(quoteFixture({ id: 1, createdAt: 'not-a-date' }))).toBeNaN();
  });
});

describe('matchesSearch', () => {
  const quote = quoteFixture({ id: 1, author: 'Grace Hopper', text: 'Ship the migration.' });

  it('matches the author case-insensitively', () => {
    expect(matchesSearch(quote, 'GRACE')).toBe(true);
  });

  it('matches the quote text', () => {
    expect(matchesSearch(quote, 'migration')).toBe(true);
  });

  it('treats a whitespace-only search as no filter', () => {
    expect(matchesSearch(quote, '   ')).toBe(true);
  });

  it('rejects a needle present in neither field', () => {
    expect(matchesSearch(quote, 'kubernetes')).toBe(false);
  });
});

describe('selectVisibleQuotes', () => {
  const older = quoteFixture({ id: 1, author: 'Ada Lovelace', createdAt: '2026-08-10T10:00:00+00:00' });
  const newer = quoteFixture({ id: 2, author: 'Grace Hopper', createdAt: '2026-08-16T14:00:00+00:00' });

  it('returns an empty list for an empty source', () => {
    expect(selectVisibleQuotes([], '', 'newest')).toEqual([]);
  });

  it('orders newest first', () => {
    expect(selectVisibleQuotes([older, newer], '', 'newest').map((q) => q.id)).toEqual([2, 1]);
  });

  it('orders oldest first', () => {
    expect(selectVisibleQuotes([older, newer], '', 'oldest').map((q) => q.id)).toEqual([1, 2]);
  });

  it('compares instants, not strings, across differing UTC offsets', () => {
    // 2026-08-11T01:00+05:30 is 2026-08-10T19:30Z — earlier than 2026-08-10T23:00Z,
    // even though it sorts later as plain text.
    const lateOnTheTenth = quoteFixture({ id: 1, createdAt: '2026-08-10T23:00:00+00:00' });
    const earlyOnTheEleventh = quoteFixture({ id: 2, createdAt: '2026-08-11T01:00:00+05:30' });

    expect(selectVisibleQuotes([lateOnTheTenth, earlyOnTheEleventh], '', 'oldest').map((q) => q.id)).toEqual(
      [2, 1],
    );
  });

  it('sorts unparsable timestamps last in both directions', () => {
    const broken = quoteFixture({ id: 3, createdAt: '' });

    expect(selectVisibleQuotes([broken, older, newer], '', 'newest').map((q) => q.id)).toEqual([2, 1, 3]);
    expect(selectVisibleQuotes([broken, older, newer], '', 'oldest').map((q) => q.id)).toEqual([1, 2, 3]);
  });

  it('breaks timestamp ties by id so the order is stable', () => {
    const first = quoteFixture({ id: 7, createdAt: '2026-08-10T10:00:00+00:00' });
    const second = quoteFixture({ id: 8, createdAt: '2026-08-10T10:00:00+00:00' });

    expect(selectVisibleQuotes([second, first], '', 'oldest').map((q) => q.id)).toEqual([7, 8]);
  });

  it('filters before sorting', () => {
    expect(selectVisibleQuotes([older, newer], 'grace', 'newest').map((q) => q.id)).toEqual([2]);
  });

  it('returns an empty list when nothing matches', () => {
    expect(selectVisibleQuotes([older, newer], 'nobody', 'newest')).toEqual([]);
  });

  it('does not mutate the source array', () => {
    const source = [older, newer];
    selectVisibleQuotes(source, '', 'newest');
    expect(source.map((q) => q.id)).toEqual([1, 2]);
  });
});

describe('selectAuthorTallies', () => {
  it('counts per author, busiest first', () => {
    const quotes = [
      quoteFixture({ id: 1, author: 'Ada Lovelace' }),
      quoteFixture({ id: 2, author: 'Ada Lovelace' }),
      quoteFixture({ id: 3, author: 'Grace Hopper' }),
    ];

    expect(selectAuthorTallies(quotes)).toEqual([
      { author: 'Ada Lovelace', count: 2 },
      { author: 'Grace Hopper', count: 1 },
    ]);
  });

  it('breaks count ties alphabetically', () => {
    const quotes = [
      quoteFixture({ id: 1, author: 'Grace Hopper' }),
      quoteFixture({ id: 2, author: 'Ada Lovelace' }),
    ];

    expect(selectAuthorTallies(quotes).map((t) => t.author)).toEqual(['Ada Lovelace', 'Grace Hopper']);
  });

  it('returns an empty list for an empty source', () => {
    expect(selectAuthorTallies([])).toEqual([]);
  });
});
