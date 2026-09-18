import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { HTTP_INTERCEPTOR_CHAIN } from '../../app.config';
import { provideQuotesApiBaseUrl } from '../config/quotes-api.config';
import { provideRetryPolicy } from '../http/retry-idempotent.interceptor';
import { TokenStore } from './token-store';

import { clearBrowserState } from '../../../testing/browser-state';

// The API returns an empty refreshToken now — the real one goes out as an HttpOnly cookie
// the browser attaches for us and this code can never read.
const ROTATED = { accessToken: 'new-access', refreshToken: '', expiresIn: 3600 };

async function settle(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe('refreshOn401Interceptor', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;
  let tokens: TokenStore;

  beforeEach(() => {
    clearBrowserState();
    TestBed.configureTestingModule({
      providers: [
        // The shipped chain, so ordering between refresh, mapping and retry is what ships.
        provideHttpClient(withInterceptors([...HTTP_INTERCEPTOR_CHAIN])),
        provideHttpClientTesting(),
        provideQuotesApiBaseUrl('/api'),
        provideRetryPolicy({ maxRetries: 0, baseDelayMs: 0, maxDelayMs: 0 }),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
    tokens = TestBed.inject(TokenStore);
    tokens.setAccessToken('old-access', 3600);
  });

  afterEach(() => httpTesting.verify({ ignoreCancelled: true }));

  it('refreshes on a 401 and replays the request with the new token', async () => {
    const pending = firstValueFrom(http.get('/api/quotes'));

    const first = httpTesting.expectOne('/api/quotes');
    expect(first.request.headers.get('Authorization')).toBe('Bearer old-access');
    first.flush('', { status: 401, statusText: 'Unauthorized' });
    await settle();

    const refresh = httpTesting.expectOne('/api/auth/refresh');
    // No token in the body, and credentials enabled so the cookie rides along.
    expect(refresh.request.body).toEqual({});
    expect(refresh.request.withCredentials).toBe(true);
    refresh.flush(ROTATED);
    await settle();

    // Replayed with the FRESH token — not the dead one it was originally stamped with.
    const replay = httpTesting.expectOne('/api/quotes');
    expect(replay.request.headers.get('Authorization')).toBe('Bearer new-access');
    replay.flush([{ id: 1 }]);

    await expect(pending).resolves.toEqual([{ id: 1 }]);
  });

  it('takes the new access token and never touches a refresh token', async () => {
    const pending = firstValueFrom(http.get('/api/quotes')).catch((e: unknown) => e);

    httpTesting.expectOne('/api/quotes').flush('', { status: 401, statusText: 'Unauthorized' });
    await settle();
    httpTesting.expectOne('/api/auth/refresh').flush(ROTATED);
    await settle();
    httpTesting.expectOne('/api/quotes').flush([]);
    await pending;

    expect(tokens.accessToken()).toBe('new-access');
    // Rotation happened in the Set-Cookie header; nothing here saw it, which is the point.
    expect(JSON.stringify(sessionStorage)).not.toContain('new-access');
  });

  /**
   * The one that matters. The API revokes the whole token family if a spent refresh token is
   * presented again (AuthEndpoints.cs:67), so two 401s must produce ONE refresh, not two.
   */
  it('coalesces concurrent 401s into a single refresh call', async () => {
    const a = firstValueFrom(http.get('/api/quotes'));
    const b = firstValueFrom(http.get('/api/quotes/1'));

    httpTesting.expectOne('/api/quotes').flush('', { status: 401, statusText: 'Unauthorized' });
    httpTesting.expectOne('/api/quotes/1').flush('', { status: 401, statusText: 'Unauthorized' });
    await settle();

    // Exactly one refresh, shared by both.
    const refresh = httpTesting.expectOne('/api/auth/refresh');
    refresh.flush(ROTATED);
    await settle();

    httpTesting.expectOne('/api/quotes').flush([{ id: 1 }]);
    httpTesting.expectOne('/api/quotes/1').flush({ id: 1 });

    await expect(a).resolves.toEqual([{ id: 1 }]);
    await expect(b).resolves.toEqual({ id: 1 });
  });

  it('signs out when the refresh token is spent', async () => {
    const pending = firstValueFrom(http.get('/api/quotes')).catch((e: unknown) => e);

    httpTesting.expectOne('/api/quotes').flush('', { status: 401, statusText: 'Unauthorized' });
    await settle();
    httpTesting
      .expectOne('/api/auth/refresh')
      .flush('', { status: 401, statusText: 'Unauthorized' });
    await pending;

    expect(tokens.isSignedIn()).toBe(false);
    expect(localStorage.getItem('quotes-web.session-until')).toBeNull();
  });

  it('does not try to refresh the refresh endpoint itself', async () => {
    const pending = firstValueFrom(http.post('/api/auth/refresh', {})).catch((e: unknown) => e);

    httpTesting
      .expectOne('/api/auth/refresh')
      .flush('', { status: 401, statusText: 'Unauthorized' });
    await pending;

    httpTesting.expectNone('/api/auth/refresh');
  });

  it('leaves non-401 failures alone', async () => {
    const pending = firstValueFrom(http.get('/api/quotes')).catch((e: unknown) => e);

    httpTesting.expectOne('/api/quotes').flush('', { status: 403, statusText: 'Forbidden' });
    await pending;

    httpTesting.expectNone('/api/auth/refresh');
    expect(tokens.isSignedIn()).toBe(true);
  });

  it('does nothing at all when no session is believed to exist', async () => {
    tokens.clear();
    const pending = firstValueFrom(http.get('/api/quotes')).catch((e: unknown) => e);

    httpTesting.expectOne('/api/quotes').flush('', { status: 401, statusText: 'Unauthorized' });
    await pending;

    httpTesting.expectNone('/api/auth/refresh');
  });
});
