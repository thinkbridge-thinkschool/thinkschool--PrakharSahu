import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { TokenStore } from '../auth/token-store';
import { authHeaderInterceptor } from './auth-header.interceptor';

import { clearBrowserState } from '../../../testing/browser-state';

describe('authHeaderInterceptor', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;
  let tokens: TokenStore;

  beforeEach(() => {
    // TokenStore restores from sessionStorage at construction, so a session written by an
    // earlier test would leak into this one and make 'signed out' cases pass wrongly.
    clearBrowserState();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authHeaderInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
    tokens = TestBed.inject(TokenStore);
  });

  afterEach(() => httpTesting.verify());

  it('sends no Authorization header while signed out', () => {
    http.get('/api/quotes').subscribe();

    const request = httpTesting.expectOne('/api/quotes');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush([]);
  });

  it('attaches the bearer token once one exists', () => {
    tokens.setAccessToken('a-token', 3600);
    http.get('/api/quotes').subscribe();

    const request = httpTesting.expectOne('/api/quotes');
    expect(request.request.headers.get('Authorization')).toBe('Bearer a-token');
    request.flush([]);
  });

  it('reads the token per request, so signing in mid-session takes effect at once', () => {
    http.get('/api/quotes').subscribe();
    httpTesting.expectOne((r) => !r.headers.has('Authorization')).flush([]);

    tokens.setAccessToken('fresh', 3600);
    http.get('/api/quotes').subscribe();
    httpTesting.expectOne((r) => r.headers.get('Authorization') === 'Bearer fresh').flush([]);
  });

  it('never sends a token to the login or refresh endpoints', () => {
    tokens.setAccessToken('a-token', 3600);

    http.post('/api/auth/login', {}).subscribe();
    httpTesting.expectOne('/api/auth/login').flush({});

    http.post('/api/auth/refresh', {}).subscribe();
    const refresh = httpTesting.expectOne('/api/auth/refresh');
    expect(refresh.request.headers.has('Authorization')).toBe(false);
    refresh.flush({});
  });

  it('does not overwrite an Authorization header the caller set deliberately', () => {
    tokens.setAccessToken('store-token', 3600);
    http.get('/api/quotes', { headers: { Authorization: 'Bearer caller-token' } }).subscribe();

    const request = httpTesting.expectOne('/api/quotes');
    expect(request.request.headers.get('Authorization')).toBe('Bearer caller-token');
    request.flush([]);
  });

  it('drops the header again after signing out', () => {
    tokens.setAccessToken('a-token', 3600);
    tokens.clear();
    http.get('/api/quotes').subscribe();

    const request = httpTesting.expectOne('/api/quotes');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush([]);
  });
});
