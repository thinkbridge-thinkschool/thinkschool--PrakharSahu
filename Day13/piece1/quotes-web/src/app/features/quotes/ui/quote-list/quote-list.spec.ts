import { ComponentFixture, TestBed } from '@angular/core/testing';

import { Quote } from '../../domain/quote';
import { QuoteList } from './quote-list';

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

describe('QuoteList', () => {
  let fixture: ComponentFixture<QuoteList>;

  function rows(): HTMLElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('[data-testid="quote-row"]'));
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [QuoteList] }).compileComponents();
    fixture = TestBed.createComponent(QuoteList);
  });

  it('renders one row per quote', async () => {
    fixture.componentRef.setInput('quotes', [
      quoteFixture({ id: 1, author: 'Ada Lovelace' }),
      quoteFixture({ id: 2, author: 'Grace Hopper' }),
    ]);
    await fixture.whenStable();

    expect(rows()).toHaveLength(2);
    expect(rows()[0].textContent).toContain('Ada Lovelace');
  });

  it('renders the empty message instead of an empty list', async () => {
    fixture.componentRef.setInput('quotes', []);
    fixture.componentRef.setInput('emptyMessage', 'Nothing matched.');
    await fixture.whenStable();

    expect(rows()).toHaveLength(0);
    expect(
      fixture.nativeElement.querySelector('[data-testid="quote-list-empty"]').textContent,
    ).toContain('Nothing matched.');
  });

  it('keeps rows that share text and author distinct, and reuses DOM on reorder', async () => {
    // Mirrors the seeded API, where ids 1 and 6 carry identical text and author.
    const first = quoteFixture({ id: 1, text: 'First quote for week 1' });
    const duplicate = quoteFixture({ id: 6, text: 'First quote for week 1' });

    fixture.componentRef.setInput('quotes', [first, duplicate]);
    await fixture.whenStable();
    expect(rows()).toHaveLength(2);

    const originalFirstRow = rows()[0];
    fixture.componentRef.setInput('quotes', [duplicate, first]);
    await fixture.whenStable();

    // `track quote.id` moves the existing element rather than rebuilding the list.
    expect(rows()[1]).toBe(originalFirstRow);
  });

  it('exposes the raw createdAt on the time element', async () => {
    fixture.componentRef.setInput('quotes', [
      quoteFixture({ id: 1, createdAt: '2026-08-16T14:00:00+00:00' }),
    ]);
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelector('time').getAttribute('datetime')).toBe(
      '2026-08-16T14:00:00+00:00',
    );
  });
});
