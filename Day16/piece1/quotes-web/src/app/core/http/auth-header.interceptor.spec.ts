import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AccessTokenStore } from '../auth/access-token.store';
import { authHeaderInterceptor } from './auth-header.interceptor';

describe('authHeaderInterceptor', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;
  let tokens: AccessTokenStore;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authHeaderInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
    tokens = TestBed.inject(AccessTokenStore);
  });

  afterEach(() => httpTesting.verify());

  it('sends no Authorization header while signed out', () => {
    http.get('/api/quotes').subscribe();

    const request = httpTesting.expectOne('/api/quotes');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush([]);
  });

  it('attaches the bearer token once one exists', () => {
    tokens.set('a-token');
    http.get('/api/quotes').subscribe();

    const request = httpTesting.expectOne('/api/quotes');
    expect(request.request.headers.get('Authorization')).toBe('Bearer a-token');
    request.flush([]);
  });

  it('reads the token per request, so signing in mid-session takes effect at once', () => {
    http.get('/api/quotes').subscribe();
    httpTesting.expectOne((r) => !r.headers.has('Authorization')).flush([]);

    tokens.set('fresh');
    http.get('/api/quotes').subscribe();
    httpTesting.expectOne((r) => r.headers.get('Authorization') === 'Bearer fresh').flush([]);
  });

  it('never sends a token to the login or refresh endpoints', () => {
    tokens.set('a-token');

    http.post('/api/auth/login', {}).subscribe();
    httpTesting.expectOne('/api/auth/login').flush({});

    http.post('/api/auth/refresh', {}).subscribe();
    const refresh = httpTesting.expectOne('/api/auth/refresh');
    expect(refresh.request.headers.has('Authorization')).toBe(false);
    refresh.flush({});
  });

  it('does not overwrite an Authorization header the caller set deliberately', () => {
    tokens.set('store-token');
    http.get('/api/quotes', { headers: { Authorization: 'Bearer caller-token' } }).subscribe();

    const request = httpTesting.expectOne('/api/quotes');
    expect(request.request.headers.get('Authorization')).toBe('Bearer caller-token');
    request.flush([]);
  });

  it('drops the header again after signing out', () => {
    tokens.set('a-token');
    tokens.clear();
    http.get('/api/quotes').subscribe();

    const request = httpTesting.expectOne('/api/quotes');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush([]);
  });
});
