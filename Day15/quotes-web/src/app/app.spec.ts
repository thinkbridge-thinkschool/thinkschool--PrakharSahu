import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { App } from './app';
import { HTTP_INTERCEPTOR_CHAIN } from './app.config';
import { provideQuotesApiBaseUrl } from './core/config/quotes-api.config';
import { provideRetryPolicy } from './core/http/retry-idempotent.interceptor';
import { RECORDED_QUOTES_200 } from './contract/week1-api.recorded';

/**
 * End-to-end through the real interceptor chain, using the recorded payloads.
 *
 * The point is what the page shows, not what the HTTP layer returns: loading, ready, empty
 * and a 4xx arriving as a sentence rather than a status code.
 */
describe('App — the states a user can actually see', () => {
  let fixture: ComponentFixture<App>;
  let httpTesting: HttpTestingController;

  const root = () => fixture.nativeElement as HTMLElement;
  const testId = (id: string) => root().querySelector(`[data-testid="${id}"]`);
  const click = (label: string) =>
    [...root().querySelectorAll('button')].find((b) => b.textContent?.includes(label))!.click();

  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    TestBed.tick();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideHttpClient(withInterceptors([...HTTP_INTERCEPTOR_CHAIN])),
        provideHttpClientTesting(),
        provideQuotesApiBaseUrl('/api'),
        provideRetryPolicy({ maxRetries: 2, baseDelayMs: 0, maxDelayMs: 0 }),
      ],
    }).compileComponents();

    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(App);
    await fixture.whenStable();
  });

  afterEach(() => httpTesting.verify());

  it('starts idle, having requested nothing', () => {
    expect(testId('idle')).toBeTruthy();
    httpTesting.expectNone(() => true);
  });

  it('shows a loading state while the request is open', async () => {
    click('GET /api/quotes');
    await settle();

    expect(testId('loading')).toBeTruthy();
    httpTesting.expectOne('/api/quotes').flush(RECORDED_QUOTES_200);
    await settle();
  });

  it('renders the recorded payload', async () => {
    click('GET /api/quotes');
    await settle();
    httpTesting.expectOne('/api/quotes').flush(RECORDED_QUOTES_200);
    await settle();

    expect(root().querySelectorAll('[data-testid="quotes"] li')).toHaveLength(3);
    expect(testId('quotes')?.textContent).toContain('First quote for week 1');
  });

  it('tells the difference between an empty list and a failure', async () => {
    click('GET /api/quotes');
    await settle();
    httpTesting.expectOne('/api/quotes').flush([]);
    await settle();

    expect(testId('empty')).toBeTruthy();
    expect(testId('error')).toBeNull();
  });

  it('surfaces a 404 with an empty body as a friendly sentence', async () => {
    click('GET /api/quotes/9999');
    await settle();
    httpTesting.expectOne('/api/quotes/9999').flush('', { status: 404, statusText: 'Not Found' });
    await settle();

    expect(testId('error-message')?.textContent?.trim()).toBe('That quote no longer exists.');
    expect(testId('error-kind')?.textContent).toBe('not-found');
    expect(testId('error')?.getAttribute('role')).toBe('alert');
  });

  it('never shows the raw text/plain exception body', async () => {
    click('GET /api/quotes');
    await settle();
    httpTesting.expectOne('/api/quotes').flush(
      'Microsoft.AspNetCore.Http.BadHttpRequestException: Failed to read parameter',
      { status: 400, statusText: 'Bad Request', headers: { 'Content-Type': 'text/plain' } },
    );
    await settle();

    const shown = root().textContent ?? '';
    expect(shown).not.toContain('BadHttpRequestException');
    expect(testId('error-message')?.textContent?.trim()).toBe(
      'The Quotes API could not accept that request.',
    );
  });

  it('retries a failing GET behind the scenes and shows the result, not the retries', async () => {
    click('GET /api/quotes');
    await settle();

    httpTesting.expectOne('/api/quotes').flush('', { status: 503, statusText: 'Unavailable' });
    await settle();
    httpTesting.expectOne('/api/quotes').flush(RECORDED_QUOTES_200);
    await settle();

    expect(root().querySelectorAll('[data-testid="quotes"] li')).toHaveLength(3);
    expect(testId('error')).toBeNull();
  });
});
