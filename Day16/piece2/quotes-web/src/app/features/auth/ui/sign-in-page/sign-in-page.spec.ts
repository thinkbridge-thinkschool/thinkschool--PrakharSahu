import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { HTTP_INTERCEPTOR_CHAIN } from '../../../../app.config';
import { routes } from '../../../../app.routes';
import { TokenStore } from '../../../../core/auth/token-store';
import { provideQuotesApiBaseUrl } from '../../../../core/config/quotes-api.config';

/**
 * Regression cover for a `withComponentInputBinding()` trap.
 *
 * An absent query parameter is bound as `undefined`, which overrides a declared input
 * default. `returnUrl` looked safe with `input('/quotes')` and was undefined in practice for
 * anyone who opened /sign-in from the nav — so a successful sign-in navigated nowhere.
 */
describe('SignInPage returnUrl', () => {
  let harness: RouterTestingHarness;
  let router: Router;
  let httpTesting: HttpTestingController;

  async function settle(turns = 1): Promise<void> {
    for (let i = 0; i < turns; i += 1) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      harness.detectChanges();
    }
  }

  /**
   * Waits for the router to actually land, rather than counting turns.
   *
   * The post-sign-in target is a lazily loaded route, so the first test to reach it pays for
   * a dynamic import while later ones hit the module cache. A fixed number of ticks passes
   * or fails depending on test ORDER, which is exactly the kind of flake worth not shipping.
   */
  async function waitForUrl(expected: string): Promise<void> {
    for (let i = 0; i < 50 && router.url !== expected; i += 1) {
      await settle();
    }
  }

  async function signIn(): Promise<void> {
    const root = harness.routeNativeElement!;
    for (const [id, value] of [
      ['#sign-in-email', 'dev@quotes.local'],
      ['#sign-in-password', 'secret'],
    ] as const) {
      const input = root.querySelector<HTMLInputElement>(id)!;
      input.value = value;
      input.dispatchEvent(new Event('input', { bubbles: true }));
    }
    await settle();
    root.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await settle();
  }

  beforeEach(async () => {
    // TokenStore restores from sessionStorage at construction, so a session written by an
    // earlier test would leak into this one and make 'signed out' cases pass wrongly.
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes, withComponentInputBinding()),
        provideHttpClient(withInterceptors([...HTTP_INTERCEPTOR_CHAIN])),
        provideHttpClientTesting(),
        provideQuotesApiBaseUrl('/api'),
      ],
    });
    router = TestBed.inject(Router);
    httpTesting = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => httpTesting.verify({ ignoreCancelled: true }));

  it('falls back to /quotes when no returnUrl is present', async () => {
    await harness.navigateByUrl('/sign-in');
    await signIn();

    httpTesting.expectOne('/api/auth/login').flush({ accessToken: 'tok', refreshToken: 'r', expiresIn: 900 });
    await waitForUrl('/quotes');

    expect(TestBed.inject(TokenStore).isSignedIn()).toBe(true);
    expect(router.url).toBe('/quotes');
    httpTesting.expectOne('/api/quotes').flush([]);
  });

  it('honours a returnUrl supplied by the guard', async () => {
    await harness.navigateByUrl('/sign-in?returnUrl=%2Fquotes%2Fnew');
    await signIn();

    httpTesting.expectOne('/api/auth/login').flush({ accessToken: 'tok', refreshToken: 'r', expiresIn: 900 });
    await waitForUrl('/quotes/new');

    expect(router.url).toBe('/quotes/new');
  });

  it('refuses an off-site returnUrl', async () => {
    await harness.navigateByUrl('/sign-in?returnUrl=%2F%2Fevil.example');
    await signIn();

    httpTesting.expectOne('/api/auth/login').flush({ accessToken: 'tok', refreshToken: 'r', expiresIn: 900 });
    await waitForUrl('/quotes');

    expect(router.url).toBe('/quotes');
    httpTesting.expectOne('/api/quotes').flush([]);
  });
});
