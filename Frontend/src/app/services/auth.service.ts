import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { Observable, of, throwError, timer } from 'rxjs';
import { catchError, finalize, map, retry, shareReplay, tap } from 'rxjs/operators';
import { AuthStore, User } from '../store/auth.store';
import { AppStore, Guild } from '../store/app.store';
import { environment } from '../../environments/environment';
import { ApiErrorService } from './api-error.service';

interface BackendSessionResponse {
    user: User;
}

interface CsrfResponse {
    token: string;
}

const LEGACY_AUTH_STORAGE_KEYS = [
    'rankoon_token',
    'rankoon_refresh_token',
    'rankoon_token_expires_at'
];

@Injectable({
    providedIn: 'root'
})
export class AuthService {
    private readonly GUILDS_CACHE_MS = 120_000;
    private readonly GUILDS_REFRESH_COOLDOWN_MS = 10_000;
    private readonly http = inject(HttpClient);
    private readonly router = inject(Router);
    private readonly authStore = inject(AuthStore);
    private readonly appStore = inject(AppStore);
    private readonly apiErrors = inject(ApiErrorService);
    private readonly API_BASE_URL = environment.apiBaseUrl;
    private refreshInFlight$: Observable<boolean> | null = null;
    private csrfToken: string | null = null;
    private csrfRequest$: Observable<string> | null = null;
    private guildsCache: { session: number; guilds: Guild[]; expiresAt: number } | null = null;
    private guildsRequest$: Observable<Guild[]> | null = null;
    private botInviteUrlRequest$: Observable<string> | null = null;
    private guildsRefreshAvailableAt = 0;
    private sessionGeneration = 0;
    private operatorAccessRequestUserId: string | null = null;

    login(returnUrl?: string): void {
        const url = new URL(`${this.API_BASE_URL}/auth/login`, window.location.origin);
        if (this.isSafeReturnUrl(returnUrl)) url.searchParams.set('returnUrl', returnUrl);
        this.http.get<{ loginUrl: string }>(url.toString(), { withCredentials: true }).subscribe({
            next: response => {
                if (response?.loginUrl) window.location.href = response.loginUrl;
            },
            error: error => this.authStore.setError(this.apiErrors.resolve(error, 'errors.loginStart').message)
        });
    }

    handleSessionCallback(): Observable<boolean> {
        return this.initializeSession();
    }

    initializeSession(): Observable<boolean> {
        this.clearLegacyAuthStorage();
        this.authStore.setLoading(true);
        this.authStore.setError(null);
        return this.validateSession().pipe(
            tap(response => this.authStore.setAuthData(response.user)),
            map(() => true),
            catchError(error => {
                if (error.status !== 401) {
                    this.clearLocalAuth();
                    return of(false);
                }
                return this.refreshToken();
            }),
            finalize(() => this.authStore.setLoading(false))
        );
    }

    refreshToken(): Observable<boolean> {
        if (this.refreshInFlight$) return this.refreshInFlight$;

        const generation = this.sessionGeneration;
        this.refreshInFlight$ = this.http.post<BackendSessionResponse>(`${this.API_BASE_URL}/auth/refresh`, undefined, { withCredentials: true }).pipe(
            map(response => {
                if (generation !== this.sessionGeneration) return false;
                this.authStore.setAuthData(response.user);
                return true;
            }),
            catchError(() => {
                if (generation === this.sessionGeneration) {
                    this.clearLocalAuth();
                    void this.router.navigate(['/login']);
                }
                return of(false);
            }),
            finalize(() => this.refreshInFlight$ = null),
            shareReplay({ bufferSize: 1, refCount: false })
        );
        return this.refreshInFlight$;
    }

    getCsrfToken(): Observable<string> {
        if (this.csrfToken) return of(this.csrfToken);
        if (this.csrfRequest$) return this.csrfRequest$;

        const generation = this.sessionGeneration;
        this.csrfRequest$ = this.http.get<CsrfResponse>(`${this.API_BASE_URL}/auth/csrf`, { withCredentials: true }).pipe(
            map(response => response.token),
            tap(token => {
                if (generation === this.sessionGeneration) this.csrfToken = token;
            }),
            finalize(() => this.csrfRequest$ = null),
            shareReplay({ bufferSize: 1, refCount: false })
        );
        return this.csrfRequest$;
    }

    renewCsrfToken(): Observable<string> {
        this.csrfToken = null;
        this.csrfRequest$ = null;
        return this.getCsrfToken();
    }

    logout(): void {
        this.http.post(`${this.API_BASE_URL}/auth/logout`, undefined, { withCredentials: true }).subscribe({
            error: error => console.warn('Logout notification failed:', error)
        });
        this.clearLocalAuth();
        void this.router.navigate(['/login']);
    }

    isAuthenticated(): boolean {
        return this.authStore.isAuthenticated();
    }

    getCurrentUser(): Observable<User | null> {
        if (!this.authStore.isAuthenticated()) return of(null);
        return this.http.get<User>(`${this.API_BASE_URL}/auth/me`, { withCredentials: true }).pipe(
            tap(user => this.authStore.setUser(user)),
            catchError(error => {
                if (error.status === 401) this.logout();
                return of(null);
            })
        );
    }

    refreshBotOperatorAccess(): void {
        const user = this.authStore.user();
        if (!user || this.operatorAccessRequestUserId === user.id) return;
        this.operatorAccessRequestUserId = user.id;
        this.http.get<{ isBotOperator: boolean }>(`${this.API_BASE_URL}/bot-management/access`).pipe(
            retry({ count: 3, delay: error => error?.status === 503 ? timer(2_000) : throwError(() => error) }),
            catchError(() => {
                this.operatorAccessRequestUserId = null;
                return of(null);
            })
        ).subscribe(access => {
            if (access && this.authStore.user()?.id === user.id) {
                this.authStore.setUser({ ...user, isBotOperator: access.isBotOperator });
            }
        });
    }

    getUserGuilds(refresh = false): Observable<Guild[]> {
        if (!this.authStore.isAuthenticated()) return of([]);

        const now = Date.now();
        const cachedGuilds = this.guildsCache;
        const cacheValid = cachedGuilds?.session === this.sessionGeneration && cachedGuilds.expiresAt > now;
        const refreshCoolingDown = refresh && this.guildsRefreshAvailableAt > now;
        if (cacheValid && (!refresh || refreshCoolingDown)) return of(cachedGuilds.guilds);
        if (this.guildsRequest$) return this.guildsRequest$;
        if (refresh) this.guildsRefreshAvailableAt = now + this.GUILDS_REFRESH_COOLDOWN_MS;

        this.guildsRequest$ = this.http.get<Guild[]>(`${this.API_BASE_URL}/auth/guilds`, {
            params: refresh ? { refresh: 'true' } : undefined
        }).pipe(
            tap(guilds => this.guildsCache = {
                session: this.sessionGeneration,
                guilds,
                expiresAt: Date.now() + this.GUILDS_CACHE_MS
            }),
            catchError(error => {
                if (error.status === 401) {
                    this.clearLocalAuth();
                    void this.router.navigate(['/login']);
                    return of([]);
                }
                return throwError(() => error);
            }),
            finalize(() => this.guildsRequest$ = null),
            shareReplay({ bufferSize: 1, refCount: false })
        );
        return this.guildsRequest$;
    }

    getBotInviteUrl(): Observable<string> {
        if (!this.botInviteUrlRequest$) {
            this.botInviteUrlRequest$ = this.http.get<{ inviteUrl: string }>(`${this.API_BASE_URL}/auth/bot-invite`).pipe(
                map(response => response.inviteUrl),
                shareReplay({ bufferSize: 1, refCount: false })
            );
        }
        return this.botInviteUrlRequest$;
    }

    clearLocalAuth(): void {
        this.sessionGeneration++;
        this.clearLegacyAuthStorage();
        this.authStore.clearAuth();
        this.appStore.clearState();
        this.csrfToken = null;
        this.csrfRequest$ = null;
        this.guildsCache = null;
        this.guildsRequest$ = null;
        this.guildsRefreshAvailableAt = 0;
        this.operatorAccessRequestUserId = null;
    }

    private validateSession(): Observable<BackendSessionResponse> {
        return this.http.get<BackendSessionResponse>(`${this.API_BASE_URL}/auth/validate`, { withCredentials: true });
    }

    private clearLegacyAuthStorage(): void {
        if (typeof window === 'undefined') return;
        for (const key of LEGACY_AUTH_STORAGE_KEYS) localStorage.removeItem(key);
    }

    private isSafeReturnUrl(returnUrl?: string): returnUrl is string {
        return !!returnUrl && returnUrl.startsWith('/') && !returnUrl.startsWith('//') && !returnUrl.includes('\\');
    }
}
