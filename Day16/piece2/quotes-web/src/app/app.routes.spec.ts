import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { HTTP_INTERCEPTOR_CHAIN } from './app.config';
import { routes } from './app.routes';
import { TokenStore } from './core/auth/token-store';
import { provideQuotesApiBaseUrl } from './core/config/quotes-api.config';
import { isValidQuoteId } from './features/quotes/routing/quote-id.guard';

const QUOTE = {
  id: 12,
  text: 'Always use CTEs instead of correlated subqueries.',
  author: 'mentor@thinkbridge.com',
  createdAt: '2026-08-12T09:00:00+00:00',
  isDeleted: false,
  userId: 2,
};

describe('routing', () => {
  let harness: RouterTestingHarness;
  let router: Router;
  let tokens: TokenStore;
  let httpTesting: HttpTestingController;

  beforeEach(async () => {
    // TokenStore restores from sessionStorage at construction, so a session written by an
    // earlier test would leak into this one and make 'signed out' cases pass wrongly.
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes, withComponentInputBinding()),
        // The REAL interceptor chain. Wiring a bare provideHttpClient() here would leave the
        // error mapper out, and the detail page would fall back to "Something went wrong."
        // instead of the mapped message — a green suite testing a configuration that ships
        // nowhere.
        provideHttpClient(withInterceptors([...HTTP_INTERCEPTOR_CHAIN])),
        provideHttpClientTesting(),
        provideQuotesApiBaseUrl('/api'),
      ],
    });
    router = TestBed.inject(Router);
    tokens = TestBed.inject(TokenStore);
    httpTesting = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => httpTesting.verify({ ignoreCancelled: true }));

  const text = () => (harness.routeNativeElement?.textContent ?? '').replace(/\s+/g, ' ');

  /**
   * The detail page loads through an async method, so its state lands a microtask after the
   * response is flushed. `detectChanges()` alone runs too early and would assert on the
   * loading state.
   */
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    harness.detectChanges();
  }

  describe('the auth guard', () => {
    it('redirects an unauthenticated visitor away from /quotes/new', async () => {
      await harness.navigateByUrl('/quotes/new');

      expect(router.url).toBe('/sign-in?returnUrl=%2Fquotes%2Fnew');
      expect(text()).toContain('Sign in');
    });

    it('carries the attempted url through so sign-in can send them back', async () => {
      await harness.navigateByUrl('/quotes/new');

      const returnUrl = router.parseUrl(router.url).queryParams['returnUrl'];
      expect(returnUrl).toBe('/quotes/new');
      expect(
        harness.routeNativeElement?.querySelector('[data-testid="return-url"]')?.textContent,
      ).toBe('/quotes/new');
    });

    it('lets a signed-in visitor straight through', async () => {
      tokens.setAccessToken('a-token', 3600);
      await harness.navigateByUrl('/quotes/new');

      expect(router.url).toBe('/quotes/new');
      expect(harness.routeNativeElement?.querySelector('[data-testid="new-quote-page"]')).toBeTruthy();
    });

    it('leaves the anonymous list and detail routes alone', async () => {
      // GET /api/quotes and GET /api/quotes/{id} carry no .RequireAuthorization() on the
      // server, so signing out must not lock either of them.
      tokens.clear();

      await harness.navigateByUrl('/quotes');
      httpTesting.expectOne('/api/quotes').flush([QUOTE]);
      expect(router.url).toBe('/quotes');

      await harness.navigateByUrl('/quotes/12');
      httpTesting.expectOne('/api/quotes/12').flush(QUOTE);
      expect(router.url).toBe('/quotes/12');
    });
  });

  describe('route order', () => {
    // Regression: with `quotes/:id` declared first, `:id` matches the literal segment
    // "new" and the create page becomes unreachable.
    it('resolves /quotes/new to the create page, not the detail page with id="new"', async () => {
      tokens.setAccessToken('a-token', 3600);
      await harness.navigateByUrl('/quotes/new');

      expect(harness.routeNativeElement?.querySelector('[data-testid="new-quote-page"]')).toBeTruthy();
      expect(harness.routeNativeElement?.querySelector('[data-testid="detail-error"]')).toBeNull();
      httpTesting.expectNone(() => true);
    });
  });

  describe('the :id parameter', () => {
    it('loads the detail route for a numeric id', async () => {
      await harness.navigateByUrl('/quotes/12');

      const request = httpTesting.expectOne('/api/quotes/12');
      expect(request.request.method).toBe('GET');
      request.flush(QUOTE);
      await settle();

      expect(text()).toContain('Quote #12');
      expect(text()).toContain('mentor@thinkbridge.com');
    });

    it('falls through to not-found for a non-integer id, without calling the API', async () => {
      await harness.navigateByUrl('/quotes/abc');

      expect(harness.routeNativeElement?.querySelector('[data-testid="not-found"]')).toBeTruthy();
      // The wildcard renders in place rather than redirecting, so the address bar still
      // shows what the user actually typed.
      expect(router.url).toBe('/quotes/abc');
      expect(
        harness.routeNativeElement?.querySelector('[data-testid="attempted-url"]')?.textContent,
      ).toContain('/quotes/abc');
      httpTesting.expectNone(() => true);
    });

    it.each(['0', '-1', '1.5', '12abc', '%20', 'null'])(
      'rejects the id %s before any request is made',
      async (id) => {
        await harness.navigateByUrl(`/quotes/${id}`);
        expect(harness.routeNativeElement?.querySelector('[data-testid="not-found"]')).toBeTruthy();
        httpTesting.expectNone(() => true);
      },
    );

    it('surfaces a real 404 from the API as a friendly message', async () => {
      await harness.navigateByUrl('/quotes/9999');
      httpTesting.expectOne('/api/quotes/9999').flush('', { status: 404, statusText: 'Not Found' });
      await settle();

      expect(
        harness.routeNativeElement?.querySelector('[data-testid="detail-error-message"]')?.textContent,
      ).toContain('That quote no longer exists.');
    });

    it('refetches when the id changes while the component is reused', async () => {
      await harness.navigateByUrl('/quotes/12');
      httpTesting.expectOne('/api/quotes/12').flush(QUOTE);
      await settle();

      await harness.navigateByUrl('/quotes/13');
      const second = httpTesting.expectOne('/api/quotes/13');
      second.flush({ ...QUOTE, id: 13, text: 'A different quote.' });
      await settle();

      expect(text()).toContain('Quote #13');
      expect(text()).toContain('A different quote.');
    });
  });

  describe('fallbacks', () => {
    it('redirects the empty path to the list', async () => {
      await harness.navigateByUrl('/');
      httpTesting.expectOne('/api/quotes').flush([]);
      expect(router.url).toBe('/quotes');
    });

    it('renders not-found in place for an unknown url, keeping the address', async () => {
      await harness.navigateByUrl('/nope/nope');

      expect(harness.routeNativeElement?.querySelector('[data-testid="not-found"]')).toBeTruthy();
      expect(router.url).toBe('/nope/nope');
    });

    it('still serves the explicit /not-found route', async () => {
      await harness.navigateByUrl('/not-found');

      expect(harness.routeNativeElement?.querySelector('[data-testid="not-found"]')).toBeTruthy();
      expect(router.url).toBe('/not-found');
    });
  });
});

describe('isValidQuoteId', () => {
  it('accepts the ids GET /api/quotes/{id:int} would serve', () => {
    for (const id of ['1', '12', '9999']) {
      expect(isValidQuoteId(id), id).toBe(true);
    }
  });

  it('rejects anything the {id:int} constraint would 404', () => {
    for (const id of ['abc', '1.5', '-1', '0', '', ' 1 ', '12abc', '1e3', undefined]) {
      expect(isValidQuoteId(id), String(id)).toBe(false);
    }
  });
});
