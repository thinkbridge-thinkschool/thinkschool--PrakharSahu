import { ComponentFixture, TestBed } from '@angular/core/testing';

import { Quote } from '../../domain/quote';
import { QuoteDetailStatus } from '../../state/quote-detail-store';
import { QuoteDetail } from './quote-detail';

const QUOTE: Quote = {
  id: 4,
  text: 'Check your git history for secrets!',
  author: 'Ada Lovelace',
  createdAt: '2026-08-17T08:30:00+00:00',
  isDeleted: false,
  userId: 2,
};

describe('QuoteDetail', () => {
  let fixture: ComponentFixture<QuoteDetail>;

  function text(): string {
    return (fixture.nativeElement as HTMLElement).textContent?.replace(/\s+/g, ' ').trim() ?? '';
  }

  function query(testId: string): HTMLElement | null {
    return (fixture.nativeElement as HTMLElement).querySelector(`[data-testid="${testId}"]`);
  }

  async function render(status: QuoteDetailStatus, inputs: Record<string, unknown> = {}) {
    fixture.componentRef.setInput('status', status);
    for (const [name, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(name, value);
    }
    await fixture.whenStable();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [QuoteDetail] }).compileComponents();
    fixture = TestBed.createComponent(QuoteDetail);
  });

  it('prompts for a selection when idle', async () => {
    await render('idle');
    expect(query('detail-idle')).toBeTruthy();
  });

  it('names the id it is loading', async () => {
    await render('loading', { quoteId: 4 });
    expect(text()).toContain('Loading quote #4');
  });

  it('renders every field the API returns', async () => {
    await render('ready', { quote: QUOTE, quoteId: 4 });

    expect(query('detail-card')).toBeTruthy();
    expect(text()).toContain('Quote #4');
    expect(text()).toContain('Check your git history for secrets!');
    expect(query('detail-author')?.textContent).toContain('Ada Lovelace');
    // userId and isDeleted are on the wire, so the detail shows them rather than pretending
    // the payload is a trimmed read model.
    expect(text()).toContain('userId');
    expect(text()).toContain('2');
    expect(text()).toContain('isDeleted');
    expect(text()).toContain('false');
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('time')?.getAttribute('datetime'),
    ).toBe('2026-08-17T08:30:00+00:00');
  });

  it('says which id is missing on a 404, with no error styling', async () => {
    await render('not-found', { quoteId: 9999 });

    expect(query('detail-not-found')).toBeTruthy();
    expect(text()).toContain('Quote #9999 no longer exists');
    expect(query('detail-error')).toBeNull();
  });

  // Regression: the failed state used to offer only "Try again", so a request that kept
  // failing left the pane stuck open with no way out.
  it('offers both retry and close when the request failed', async () => {
    await render('failed', { quoteId: 4, failureMessage: 'The Quotes API failed with status 500.' });

    expect(query('detail-error')).toBeTruthy();
    expect(text()).toContain('Quote #4 could not be loaded');
    expect(text()).toContain('status 500');
    expect(query('detail-retry')).toBeTruthy();
    expect(query('detail-close')).toBeTruthy();
  });

  it('emits retry and close from the failed state', async () => {
    await render('failed', { quoteId: 4, failureMessage: 'boom' });

    let retried = 0;
    let closed = 0;
    fixture.componentInstance.retryRequested.subscribe(() => (retried += 1));
    fixture.componentInstance.closeRequested.subscribe(() => (closed += 1));

    query('detail-retry')?.click();
    query('detail-close')?.click();

    expect(retried).toBe(1);
    expect(closed).toBe(1);
  });
});
