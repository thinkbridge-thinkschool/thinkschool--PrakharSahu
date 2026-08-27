import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { HTTP_INTERCEPTOR_CHAIN } from '../../../../app.config';
import { routes } from '../../../../app.routes';
import { TokenStore } from '../../../../core/auth/token-store';
import { provideQuotesApiBaseUrl } from '../../../../core/config/quotes-api.config';
import { provideRetryPolicy } from '../../../../core/http/retry-idempotent.interceptor';

const CREATED = {
  id: 42,
  text: 'Signals first.',
  author: 'dev@quotes.local',
  createdAt: '2026-08-27T09:00:00+00:00',
  isDeleted: false,
  userId: 1,
};

describe('NewQuotePage', () => {
  let harness: RouterTestingHarness;
  let router: Router;
  let httpTesting: HttpTestingController;

  async function settle(turns = 1): Promise<void> {
    for (let i = 0; i < turns; i += 1) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      harness.detectChanges();
    }
  }

  /** Exact match, not startsWith: '/quotes/new' also starts with '/quotes'. */
  async function waitForUrl(expected: string): Promise<void> {
    for (let i = 0; i < 50 && router.url !== expected; i += 1) {
      await settle();
    }
  }

  const root = () => harness.routeNativeElement!;

  async function fill(author: string, text: string): Promise<void> {
    for (const [id, value] of [
      ['#create-quote-author', author],
      ['#create-quote-text', text],
    ] as const) {
      const field = root().querySelector<HTMLInputElement | HTMLTextAreaElement>(id)!;
      field.value = value;
      field.dispatchEvent(new Event('input', { bubbles: true }));
    }
    await settle();
  }

  async function submitForm(): Promise<void> {
    root().querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await settle();
  }

  beforeEach(async () => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes, withComponentInputBinding()),
        provideHttpClient(withInterceptors([...HTTP_INTERCEPTOR_CHAIN])),
        provideHttpClientTesting(),
        provideQuotesApiBaseUrl('/api'),
        provideRetryPolicy({ maxRetries: 0, baseDelayMs: 0, maxDelayMs: 0 }),
      ],
    });
    router = TestBed.inject(Router);
    httpTesting = TestBed.inject(HttpTestingController);
    TestBed.inject(TokenStore).setAccessToken('a-token', 3600);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/quotes/new');
  });

  afterEach(() => httpTesting.verify({ ignoreCancelled: true }));

  it('posts exactly author and text, trimmed', async () => {
    await fill('  dev@quotes.local  ', '  Signals first.  ');
    await submitForm();

    const request = httpTesting.expectOne('/api/quotes');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ author: 'dev@quotes.local', text: 'Signals first.' });
    request.flush(CREATED, { status: 201, statusText: 'Created' });

    await waitForUrl('/quotes?created=42');
    httpTesting.expectOne('/api/quotes').flush([CREATED]);
  });

  /**
   * Regression. A successful save resets the form to empty, which makes `required` fire
   * again — so checking "is anything invalid?" before checking success swallowed the
   * navigation and left the user staring at a blank form after a 201.
   */
  it('navigates to the list after a 201, even though the reset form is invalid', async () => {
    await fill('dev@quotes.local', 'Signals first.');
    await submitForm();
    httpTesting.expectOne('/api/quotes').flush(CREATED, { status: 201, statusText: 'Created' });

    await waitForUrl('/quotes?created=42');

    expect(router.url).toBe('/quotes?created=42');
    httpTesting.expectOne('/api/quotes').flush([CREATED]);
  });

  it('does not send anything while the form is invalid', async () => {
    await submitForm();

    httpTesting.expectNone('/api/quotes');
    expect(root().querySelector('[data-testid="form-errors"]')).toBeTruthy();
    expect(router.url).toBe('/quotes/new');
  });

  it('rejects whitespace-only input the way the server does', async () => {
    await fill('   ', '\t\n ');
    await submitForm();

    httpTesting.expectNone('/api/quotes');
    expect(root().querySelector('[data-testid="form-errors"]')?.textContent).toContain(
      'Enter an author to credit.',
    );
  });

  it("shows the server's own message on a 400 and stays put", async () => {
    await fill('dev@quotes.local', 'Signals first.');
    await submitForm();
    httpTesting
      .expectOne('/api/quotes')
      .flush(
        { message: 'Text must be between 1 and 1000 characters.' },
        { status: 400, statusText: 'Bad Request' },
      );
    await settle(3);

    expect(root().querySelector('[data-testid="create-failed"]')?.textContent).toContain(
      'Text must be between 1 and 1000 characters.',
    );
    expect(router.url).toBe('/quotes/new');
  });
});
