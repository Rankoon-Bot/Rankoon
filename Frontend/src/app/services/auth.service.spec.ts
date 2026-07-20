import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { environment } from '../../environments/environment';
import { AuthStore, User } from '../store/auth.store';
import { testI18n } from '../testing/i18n-testing';
import { authInterceptor } from '../interceptors/auth.interceptor';
import { ACCESS_TOKEN_EXPIRATION_STORAGE_KEY, ACCESS_TOKEN_STORAGE_KEY, AuthService, REFRESH_TOKEN_STORAGE_KEY } from './auth.service';

describe('AuthService token contracts', () => {
  const user: User = { id: 'user-1', discordId: 'discord-1', username: 'user', displayName: 'User', avatar: 'avatar' };
  const router = jasmine.createSpyObj<Router>('Router', ['navigate']);
  let service: AuthService;
  let http: HttpTestingController;
  let store: AuthStore;
  let client: HttpClient;

  beforeEach(() => {
    localStorage.clear();
    router.navigate.and.resolveTo(true);
    TestBed.configureTestingModule({
      imports: [testI18n],
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting(), { provide: Router, useValue: router }]
    });
    service = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(AuthStore);
    client = TestBed.inject(HttpClient);
  });

  afterEach(() => { http.verify(); localStorage.clear(); router.navigate.calls.reset(); });

  it('persists callback access and refresh tokens after access-token validation', () => {
    service.handleTokenCallback('callback-access', 'callback-refresh').subscribe(result => expect(result).toBeTrue());
    const request = http.expectOne(`${environment.apiBaseUrl}/auth/validate`);
    expect(store.token()).toBe('callback-access');
    request.flush({ token: 'callback-access', user, expiresAt: '2026-07-19T12:00:00Z' });

    expect(localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBe('callback-access');
    expect(localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBe('callback-refresh');
    expect(store.user()).toEqual(user);
  });

  it('restores a persisted session only after server-side token validation', () => {
    localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'persisted-access');

    service.initializeSession().subscribe(result => expect(result).toBeTrue());
    const request = http.expectOne(`${environment.apiBaseUrl}/auth/validate`);
    expect(request.request.headers.get('Authorization')).toBe('Bearer persisted-access');
    expect(store.isAuthenticated()).toBeFalse();
    request.flush({ token: 'validated-access', user, expiresAt: '2026-07-19T12:00:00Z' });

    expect(store.token()).toBe('validated-access');
    expect(store.user()).toEqual(user);
  });

  it('refreshes an expired persisted access token before allowing the session', () => {
    localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'expired-access');
    localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'valid-refresh');

    service.initializeSession().subscribe(result => expect(result).toBeTrue());
    http.expectOne(`${environment.apiBaseUrl}/auth/validate`).flush(
      { errorKey: 'auth.tokenInvalid', message: 'Expired token' },
      { status: 401, statusText: 'Unauthorized' }
    );
    const refreshRequest = http.expectOne(`${environment.apiBaseUrl}/auth/refresh`);
    expect(refreshRequest.request.body).toEqual({ refreshToken: 'valid-refresh' });
    refreshRequest.flush({ accessToken: 'new-access', refreshToken: 'new-refresh', user, expiresAt: '2026-07-19T13:00:00Z' });

    expect(store.isAuthenticated()).toBeTrue();
    expect(localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBe('new-access');
  });

  it('sends the refresh-token request shape and rotates both returned tokens', () => {
    store.setAuthData(user, 'old-access');
    localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'old-access');
    localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'old-refresh');

    service.refreshToken().subscribe(result => expect(result).toBeTrue());
    const request = http.expectOne(`${environment.apiBaseUrl}/auth/refresh`);
    expect(request.request.body).toEqual({ refreshToken: 'old-refresh' });
    request.flush({ accessToken: 'new-access', refreshToken: 'new-refresh', user, expiresAt: '2026-07-19T13:00:00Z' });

    expect(store.token()).toBe('new-access');
    expect(localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBe('new-access');
    expect(localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBe('new-refresh');
  });

  it('refreshes an access token that is close to expiry before sending an API request', () => {
    store.setAuthData(user, 'expiring-access');
    localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'valid-refresh');
    localStorage.setItem(ACCESS_TOKEN_EXPIRATION_STORAGE_KEY, new Date(Date.now() + 30_000).toISOString());

    client.get('/api/protected').subscribe();
    const refreshRequest = http.expectOne(`${environment.apiBaseUrl}/auth/refresh`);
    expect(refreshRequest.request.headers.has('Authorization')).toBeFalse();
    refreshRequest.flush({ accessToken: 'new-access', refreshToken: 'new-refresh', user, expiresAt: '2099-01-01T00:00:00Z' });

    const protectedRequest = http.expectOne('/api/protected');
    expect(protectedRequest.request.headers.get('Authorization')).toBe('Bearer new-access');
    protectedRequest.flush({});
  });

  it('silently refreshes and retries an API request rejected with 401', () => {
    store.setAuthData(user, 'current-access');
    localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'valid-refresh');
    localStorage.setItem(ACCESS_TOKEN_EXPIRATION_STORAGE_KEY, '2099-01-01T00:00:00Z');

    client.get('/api/protected').subscribe();
    http.expectOne('/api/protected').flush({}, { status: 401, statusText: 'Unauthorized' });
    http.expectOne(`${environment.apiBaseUrl}/auth/refresh`).flush({ accessToken: 'new-access', refreshToken: 'new-refresh', user, expiresAt: '2099-01-01T01:00:00Z' });

    const retriedRequest = http.expectOne('/api/protected');
    expect(retriedRequest.request.headers.get('Authorization')).toBe('Bearer new-access');
    retriedRequest.flush({});
  });

  it('sends the refresh token on logout and immediately clears local authentication', () => {
    store.setAuthData(user, 'access');
    localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'access');
    localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'refresh');

    service.logout();
    const request = http.expectOne(`${environment.apiBaseUrl}/auth/logout`);
    expect(request.request.body).toEqual({ refreshToken: 'refresh' });
    expect(localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBeNull();
    expect(localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBeNull();
    expect(store.token()).toBeNull();
    expect(store.user()).toBeNull();
    expect(router.navigate).toHaveBeenCalledWith(['/login']);
    request.flush({ messageKey: 'auth.logoutSucceeded', message: 'Logged out successfully.' });
  });

  it('clears both token types when callback validation fails', () => {
    localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'stale-access');
    localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'stale-refresh');
    service.handleTokenCallback('invalid-access', 'new-refresh').subscribe(result => expect(result).toBeFalse());
    http.expectOne(`${environment.apiBaseUrl}/auth/validate`).flush({ errorKey: 'auth.tokenInvalid', message: 'Invalid token' }, { status: 401, statusText: 'Unauthorized' });

    expect(localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBeNull();
    expect(localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBeNull();
    expect(store.token()).toBeNull();
  });

  it('clears both token types and returns to login when refresh fails', () => {
    store.setAuthData(user, 'access');
    localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'access');
    localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'invalid-refresh');
    service.refreshToken().subscribe(result => expect(result).toBeFalse());
    http.expectOne(`${environment.apiBaseUrl}/auth/refresh`).flush({ errorKey: 'auth.refreshTokenInvalid', message: 'Refresh expired' }, { status: 401, statusText: 'Unauthorized' });

    expect(localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBeNull();
    expect(localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBeNull();
    expect(store.token()).toBeNull();
    expect(router.navigate).toHaveBeenCalledWith(['/login']);
  });
});
