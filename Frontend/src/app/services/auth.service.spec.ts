import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { Router } from '@angular/router';
import { environment } from '../../environments/environment';
import { AuthStore, User } from '../store/auth.store';
import { Guild } from '../store/app.store';
import { testI18n } from '../testing/i18n-testing';
import { authInterceptor } from '../interceptors/auth.interceptor';
import { AuthService } from './auth.service';

describe('AuthService cookie-session contracts', () => {
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

  it('bootstraps the session from a credentialed token-free validation response', () => {
    localStorage.setItem('rankoon_token', 'legacy-access');
    service.initializeSession().subscribe(result => expect(result).toBeTrue());
    const request = http.expectOne(`${environment.apiBaseUrl}/auth/validate`);
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.headers.has('Authorization')).toBeFalse();
    request.flush({ user });

    expect(store.user()).toEqual(user);
    expect(store.isAuthenticated()).toBeTrue();
    expect(localStorage.getItem('rankoon_token')).toBeNull();
  });

  it('refreshes a rejected validation once using cookie credentials and no request body', () => {
    service.initializeSession().subscribe(result => expect(result).toBeTrue());
    http.expectOne(`${environment.apiBaseUrl}/auth/validate`).flush({}, { status: 401, statusText: 'Unauthorized' });
    http.expectOne(`${environment.apiBaseUrl}/auth/csrf`).flush({ token: 'csrf' });
    const refresh = http.expectOne(`${environment.apiBaseUrl}/auth/refresh`);
    expect(refresh.request.body).toBeNull();
    expect(refresh.request.headers.get('X-CSRF-Token')).toBe('csrf');
    refresh.flush({ user });

    expect(store.user()).toEqual(user);
  });

  it('coalesces concurrent refresh requests', () => {
    const results: boolean[] = [];
    service.refreshToken().subscribe(result => results.push(result));
    service.refreshToken().subscribe(result => results.push(result));
    http.expectOne(`${environment.apiBaseUrl}/auth/csrf`).flush({ token: 'csrf' });
    http.expectOne(`${environment.apiBaseUrl}/auth/refresh`).flush({ user });

    expect(results).toEqual([true, true]);
  });

  it('adds cookie credentials and a memory-only CSRF header to unsafe API requests', () => {
    client.post('/api/protected', {}).subscribe();
    http.expectOne(`${environment.apiBaseUrl}/auth/csrf`).flush({ token: 'csrf' });
    const request = http.expectOne('/api/protected');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.headers.get('X-CSRF-Token')).toBe('csrf');
    expect(request.request.headers.has('Authorization')).toBeFalse();
    request.flush({});

    client.post('/api/another-protected', {}).subscribe();
    const cachedRequest = http.expectOne('/api/another-protected');
    expect(cachedRequest.request.headers.get('X-CSRF-Token')).toBe('csrf');
    cachedRequest.flush({});
  });

  it('silently refreshes and retries a rejected API request without bearer credentials', () => {
    client.get('/api/protected').subscribe();
    http.expectOne('/api/protected').flush({}, { status: 401, statusText: 'Unauthorized' });
    http.expectOne(`${environment.apiBaseUrl}/auth/csrf`).flush({ token: 'csrf' });
    http.expectOne(`${environment.apiBaseUrl}/auth/refresh`).flush({ user });
    const retried = http.expectOne('/api/protected');
    expect(retried.request.withCredentials).toBeTrue();
    expect(retried.request.headers.has('Authorization')).toBeFalse();
    retried.flush({});
  });

  it('clears known legacy auth keys without reading or sending their values', () => {
    localStorage.setItem('rankoon_token', 'legacy-access');
    localStorage.setItem('rankoon_refresh_token', 'legacy-refresh');
    localStorage.setItem('rankoon_token_expires_at', 'legacy-expiry');
    store.setAuthData(user);

    service.clearLocalAuth();

    expect(localStorage.getItem('rankoon_token')).toBeNull();
    expect(localStorage.getItem('rankoon_refresh_token')).toBeNull();
    expect(localStorage.getItem('rankoon_token_expires_at')).toBeNull();
    expect(store.user()).toBeNull();
  });

  it('logs out with cookie credentials and no body', () => {
    store.setAuthData(user);
    service.logout();
    http.expectOne(`${environment.apiBaseUrl}/auth/csrf`).flush({ token: 'csrf' });
    const request = http.expectOne(`${environment.apiBaseUrl}/auth/logout`);
    expect(request.request.body).toBeNull();
    expect(request.request.withCredentials).toBeTrue();
    request.flush({});

    expect(store.user()).toBeNull();
    expect(router.navigate).toHaveBeenCalledWith(['/login']);
  });

  it('caches guild requests and throttles forced refreshes per session', fakeAsync(() => {
    const guilds: Guild[] = [{
      id: 'guild-1', name: 'Guild', icon: null, owner: true, permissions: '8', features: [], botInstalled: true, inviteUrl: ''
    }];
    store.setAuthData(user);

    service.getUserGuilds().subscribe(result => expect(result).toEqual(guilds));
    http.expectOne(`${environment.apiBaseUrl}/auth/guilds`).flush(guilds);
    service.getUserGuilds().subscribe(result => expect(result).toEqual(guilds));
    http.expectNone(`${environment.apiBaseUrl}/auth/guilds`);
    service.getUserGuilds(true).subscribe(result => expect(result).toEqual(guilds));
    http.expectOne(request => request.url === `${environment.apiBaseUrl}/auth/guilds` && request.params.get('refresh') === 'true').flush(guilds);
    tick(120_001);
    service.getUserGuilds().subscribe(result => expect(result).toEqual(guilds));
    http.expectOne(`${environment.apiBaseUrl}/auth/guilds`).flush(guilds);
  }));
});
