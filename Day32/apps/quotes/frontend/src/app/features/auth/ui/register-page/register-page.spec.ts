import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { HTTP_INTERCEPTOR_CHAIN } from '../../../../app.config';
import { routes } from '../../../../app.routes';
import { TokenStore } from '../../../../core/auth/token-store';
import { provideQuotesApiBaseUrl } from '../../../../core/config/quotes-api.config';

import { clearBrowserState } from '../../../../../testing/browser-state';

/** What the API returns from POST /api/auth/register: 201, and a live session. */
const CREATED = { accessToken: 'new-account-token', refreshToken: '', expiresIn: 7200 };

describe('RegisterPage', () => {
  let harness: RouterTestingHarness;
  let router: Router;
  let httpTesting: HttpTestingController;

  async function settle(turns = 1): Promise<void> {
    for (let i = 0; i < turns; i += 1) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      harness.detectChanges();
    }
  }

  // Waits for the router to land rather than counting ticks: the target is lazily loaded, so
  // a fixed tick count passes or fails depending on test ORDER.
  async function waitForUrl(expected: string): Promise<void> {
    for (let i = 0; i < 50 && router.url !== expected; i += 1) {
      await settle();
    }
  }

  function root(): HTMLElement {
    return harness.routeNativeElement!;
  }

  async function fill(email: string, password: string): Promise<void> {
    for (const [selector, value] of [
      ['#register-email', email],
      ['#register-password', password],
    ] as const) {
      const field = root().querySelector<HTMLInputElement>(selector)!;
      field.value = value;
      field.dispatchEvent(new Event('input', { bubbles: true }));
    }
    await settle();
  }

  async function submit(): Promise<void> {
    root()
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await settle();
  }

  async function register(email = 'new.user@example.com', password = 'a-good-password'): Promise<void> {
    await fill(email, password);
    await submit();
  }

  beforeEach(async () => {
    clearBrowserState();
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes, withComponentInputBinding()),
        // The real interceptor chain, so a 409 arrives as an ApiError exactly as in the app.
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

  it('posts the credentials to the register endpoint', async () => {
    await harness.navigateByUrl('/register');
    await register('New.User@Example.com ', 'a-good-password');

    const request = httpTesting.expectOne('/api/auth/register');
    expect(request.request.method).toBe('POST');
    // Casing is sent verbatim: lowercasing the email is the server's job, and doing it in two
    // places is how two definitions of "the same account" drift apart.
    //
    // The trailing space is absent because `<input type="email">` sanitises its own value —
    // the browser strips leading and trailing whitespace before anything reads it. Worth
    // knowing rather than assuming: it means the client cannot send a padded email even by
    // accident, but also that a client-side trim() would be testing the browser, not the app.
    expect(request.request.body).toEqual({
      email: 'New.User@Example.com',
      password: 'a-good-password',
    });
    // Cookies must be attached, or the refresh cookie the API sets is silently dropped.
    expect(request.request.withCredentials).toBe(true);

    request.flush(CREATED, { status: 201, statusText: 'Created' });
    await waitForUrl('/quotes');
    httpTesting.expectOne('/api/quotes').flush([]);
  });

  it('lands signed in, without a second trip through the sign-in form', async () => {
    await harness.navigateByUrl('/register');
    await register();

    httpTesting.expectOne('/api/auth/register').flush(CREATED, { status: 201, statusText: 'Created' });
    await waitForUrl('/quotes');

    const tokens = TestBed.inject(TokenStore);
    expect(tokens.isSignedIn()).toBe(true);
    expect(tokens.accessToken()).toBe('new-account-token');
    expect(router.url).toBe('/quotes');
    httpTesting.expectOne('/api/quotes').flush([]);
  });

  it('explains a taken email instead of showing a status code', async () => {
    await harness.navigateByUrl('/register');
    await register();

    httpTesting
      .expectOne('/api/auth/register')
      .flush(
        { message: 'An account with that email already exists.' },
        { status: 409, statusText: 'Conflict' },
      );
    await settle();

    expect(root().querySelector('[data-testid="register-error"]')?.textContent?.trim()).toBe(
      'An account with that email already exists.',
    );
    expect(TestBed.inject(TokenStore).isSignedIn()).toBe(false);
    expect(router.url).toBe('/register');
  });

  // ASP.NET's ValidationProblem puts the useful sentence in `errors`, not in `title`.
  it('surfaces the server field message from a validation failure', async () => {
    await harness.navigateByUrl('/register');
    await register();

    httpTesting.expectOne('/api/auth/register').flush(
      {
        title: 'One or more validation errors occurred.',
        errors: { registration: ['Password must be at least 8 characters.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle();

    expect(root().querySelector('[data-testid="register-error"]')?.textContent?.trim()).toBe(
      'Password must be at least 8 characters.',
    );
  });

  it('will not submit a password shorter than the server would accept', async () => {
    await harness.navigateByUrl('/register');
    await register('new.user@example.com', 'short');

    // No request at all — the round trip is skipped, not merely ignored on return.
    httpTesting.expectNone('/api/auth/register');
    expect(root().querySelector('[data-testid="password-hint"]')?.textContent).toContain('8');
  });

  it('honours a returnUrl, and falls back to /quotes when it is absent', async () => {
    await harness.navigateByUrl('/register?returnUrl=%2Fquotes%2Fnew');
    await register();

    httpTesting.expectOne('/api/auth/register').flush(CREATED, { status: 201, statusText: 'Created' });
    await waitForUrl('/quotes/new');

    expect(router.url).toBe('/quotes/new');
  });

  // The `withComponentInputBinding()` trap that has already bitten this codebase twice: an
  // absent query parameter binds as `undefined` and overrides the input's declared default.
  it('does not strand the user when no returnUrl is present', async () => {
    await harness.navigateByUrl('/register');
    await register();

    httpTesting.expectOne('/api/auth/register').flush(CREATED, { status: 201, statusText: 'Created' });
    await waitForUrl('/quotes');

    expect(router.url).toBe('/quotes');
    httpTesting.expectOne('/api/quotes').flush([]);
  });

  it('refuses an off-site returnUrl', async () => {
    await harness.navigateByUrl('/register?returnUrl=%2F%2Fevil.example');
    await register();

    httpTesting.expectOne('/api/auth/register').flush(CREATED, { status: 201, statusText: 'Created' });
    await waitForUrl('/quotes');

    expect(router.url).toBe('/quotes');
    httpTesting.expectOne('/api/quotes').flush([]);
  });
});
