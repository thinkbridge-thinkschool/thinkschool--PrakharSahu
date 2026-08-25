import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { accessTokenInterceptor } from './access-token.interceptor';
import { AccessTokenStore } from './access-token.store';

describe('accessTokenInterceptor', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;
  let tokens: AccessTokenStore;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([accessTokenInterceptor])),
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

  it('reads the token per request, so signing in mid-session takes effect immediately', () => {
    http.get('/api/quotes').subscribe();
    httpTesting.expectOne((r) => !r.headers.has('Authorization')).flush([]);

    tokens.set('fresh-token');
    http.get('/api/quotes').subscribe();
    httpTesting.expectOne((r) => r.headers.get('Authorization') === 'Bearer fresh-token').flush([]);
  });

  it('never sends a token to the login endpoint', () => {
    tokens.set('a-token');
    http.post('/api/auth/login', { email: 'someone@example.test', password: 'x' }).subscribe();

    const request = httpTesting.expectOne('/api/auth/login');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush({ accessToken: 'new', refreshToken: 'r', expiresIn: 900 });
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
