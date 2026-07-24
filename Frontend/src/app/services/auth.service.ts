import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { Observable, of, throwError, timer } from 'rxjs';
import { catchError, finalize, map, retry, shareReplay, tap } from 'rxjs/operators';
import { AuthStore, User } from '../store/auth.store';
import { AppStore, Guild } from '../store/app.store';
import { environment } from '../../environments/environment';
import { ApiErrorService } from './api-error.service';

export interface BackendTokenResponse {
    token: string;
    user: User;
    expiresAt: string;
}

export interface BackendRefreshResponse {
    accessToken: string;
    refreshToken: string;
    user: User;
    expiresAt: string;
}

export const ACCESS_TOKEN_STORAGE_KEY = 'rankoon_token';
export const REFRESH_TOKEN_STORAGE_KEY = 'rankoon_refresh_token';
export const ACCESS_TOKEN_EXPIRATION_STORAGE_KEY = 'rankoon_token_expires_at';

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

    private readonly DISCORD_CLIENT_ID = environment.discordClientId;
    private readonly DISCORD_REDIRECT_URI = environment.discordRedirectUri; // Backend URL
    private readonly API_BASE_URL = environment.apiBaseUrl;
    private readonly TOKEN_REFRESH_BUFFER_MS = 60_000;
    private refreshInFlight$: Observable<boolean> | null = null;
    private guildsCache: { token: string; guilds: Guild[]; expiresAt: number } | null = null;
    private guildsRequest$: Observable<Guild[]> | null = null;
    private botInviteUrlRequest$: Observable<string> | null = null;
    private guildsRefreshAvailableAt = 0;
    private authGeneration = 0;
    private operatorAccessRequestToken: string | null = null;

    /**
     * Initiates Discord OAuth2 login flow - gets login URL from backend
     */
    login(returnUrl?: string): void {
        const url = new URL(`${this.API_BASE_URL}/auth/login`, window.location.origin);
        if (this.isSafeReturnUrl(returnUrl)) {
            url.searchParams.set('returnUrl', returnUrl);
        }
        this.http.get<{ loginUrl: string }>(url.toString()).subscribe({
            next: (res) => {
                if (res?.loginUrl) {
                    window.location.href = res.loginUrl;
                }
            },
            error: (err) => {
                this.authStore.setError(this.apiErrors.resolve(err, 'errors.loginStart').message);
            }
        });
    }

    /**
     * Handles callback with backend token from query parameter
     */
    handleTokenCallback(token: string, refreshToken: string): Observable<boolean> {
        this.authStore.setLoading(true);
        this.authStore.setError(null);

        this.authStore.setToken(token);
        return this.validateBackendToken().pipe(
            tap(response => {
                this.saveTokenToStorage(response.token);
                this.saveTokenExpirationToStorage(response.expiresAt);
                this.saveRefreshTokenToStorage(refreshToken);
                this.authStore.setToken(response.token);
                this.authStore.setUser(response.user);
                this.authStore.setLoading(false);
            }),
            map(() => true),
            catchError(error => {
                this.clearLocalAuth();
                this.authStore.setError(this.apiErrors.resolve(error, 'errors.signIn').message);
                this.authStore.setLoading(false);
                return of(false);
            })
        );
    }

    /**
     * Validates backend token and gets user info
     */
    private validateBackendToken(): Observable<BackendTokenResponse> {
        return this.http.get<BackendTokenResponse>(`${this.API_BASE_URL}/auth/validate`);
    }

    /**
     * Refreshes the backend token
     */
    refreshToken(): Observable<boolean> {
        if (this.refreshInFlight$) {
            return this.refreshInFlight$;
        }

        const refreshToken = this.loadRefreshTokenFromStorage();
        if (!refreshToken) {
            this.clearLocalAuth();
            return of(false);
        }

        const authGeneration = this.authGeneration;
        this.refreshInFlight$ = this.http.post<BackendRefreshResponse>(`${this.API_BASE_URL}/auth/refresh`, {
            refreshToken
        }).pipe(
            map(response => {
                if (authGeneration !== this.authGeneration) {
                    return false;
                }

                this.saveTokenToStorage(response.accessToken);
                this.saveTokenExpirationToStorage(response.expiresAt);
                this.saveRefreshTokenToStorage(response.refreshToken);
                this.authStore.setToken(response.accessToken);
                this.authStore.setUser(response.user);
                return true;
            }),
            catchError(error => {
                if (authGeneration === this.authGeneration) {
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

    /**
     * Returns a usable access token, refreshing it before it expires.
     */
    ensureValidAccessToken(): Observable<string | null> {
        const token = this.authStore.token();
        if (!token || !this.isTokenExpiringSoon(token)) {
            return of(token);
        }

        return this.refreshToken().pipe(
            map(refreshed => refreshed ? this.authStore.token() : null)
        );
    }

    /**
     * Restores a persisted session before the router evaluates protected routes.
     */
    initializeSession(): Observable<boolean> {
        const token = this.loadTokenFromStorage();
        if (!token) {
            return this.refreshToken();
        }

        this.authStore.setLoading(true);
        this.authStore.setToken(token);
        return this.validateBackendToken().pipe(
            tap(response => {
                this.saveTokenToStorage(response.token);
                this.saveTokenExpirationToStorage(response.expiresAt);
                this.authStore.setAuthData(response.user, response.token);
            }),
            map(() => true),
            catchError(error => {
                if (error.status !== 401) {
                    this.clearLocalAuth();
                    return of(false);
                }

                return this.refreshToken();
            }),
            tap(() => this.authStore.setLoading(false))
        );
    }

    /**
     * Logs out the user
     */
    logout(): void {
        const refreshToken = this.loadRefreshTokenFromStorage();

        if (refreshToken) {
            this.http.post(`${this.API_BASE_URL}/auth/logout`, { refreshToken }).subscribe({
                error: (error) => console.warn('Logout notification failed:', error)
            });
        }

        this.clearLocalAuth();
        void this.router.navigate(['/login']);
    }

    /**
     * Checks if user is authenticated
     */
    isAuthenticated(): boolean {
        return this.authStore.isAuthenticated();
    }

    /**
     * Gets current user info from backend
     */
    getCurrentUser(): Observable<User | null> {
        const token = this.authStore.token();
        if (!token) {
            return of(null);
        }

        return this.http.get<User>(`${this.API_BASE_URL}/auth/me`, {
            headers: {
                'Authorization': `Bearer ${token}`
            }
        }).pipe(
            tap(user => this.authStore.setUser(user)),
            catchError(error => {
                console.error('Failed to fetch user info:', error);
                if (error.status === 401) {
                    this.logout();
                }
                return of(null);
            })
        );
    }

    refreshBotOperatorAccess(): void {
        const token = this.authStore.token();
        const user = this.authStore.user();
        if (!token || !user || this.operatorAccessRequestToken === token) return;
        this.operatorAccessRequestToken = token;
        this.http.get<{ isBotOperator: boolean }>(`${this.API_BASE_URL}/bot-management/access`).pipe(
            retry({ count: 3, delay: error => error?.status === 503 ? timer(2_000) : throwError(() => error) }),
            catchError(() => {
                this.operatorAccessRequestToken = null;
                return of(null);
            })
        ).subscribe(access => {
            if (access && this.authStore.token() === token) this.authStore.setUser({ ...user, isBotOperator: access.isBotOperator });
        });
    }

    /**
     * Gets user's Discord guilds from backend
     */
    getUserGuilds(refresh = false): Observable<Guild[]> {
        const token = this.authStore.token();
        if (!token) {
            return of([]);
        }

        const now = Date.now();
        const cachedGuilds = this.guildsCache;
        const cacheValid = cachedGuilds?.token === token && cachedGuilds.expiresAt > now;
        const refreshCoolingDown = refresh && this.guildsRefreshAvailableAt > now;
        if (cacheValid && (!refresh || refreshCoolingDown)) {
            return of(cachedGuilds.guilds);
        }

        if (this.guildsRequest$) {
            return this.guildsRequest$;
        }

        if (refresh) {
            this.guildsRefreshAvailableAt = now + this.GUILDS_REFRESH_COOLDOWN_MS;
        }

        this.guildsRequest$ = this.http.get<Guild[]>(`${this.API_BASE_URL}/auth/guilds`, {
            headers: {
                'Authorization': `Bearer ${token}`
            },
            params: refresh ? { refresh: 'true' } : undefined
        }).pipe(
            tap(guilds => this.guildsCache = {
                token,
                guilds,
                expiresAt: Date.now() + this.GUILDS_CACHE_MS
            }),
            catchError(error => {
                console.error('Failed to fetch user guilds:', error);
                if (error.status === 401) {
                    this.logout();
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

    /**
     * Loads token from localStorage and validates it
     */
    private loadTokenFromStorage(): string | null {
        return typeof window === 'undefined' ? null : localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY);
    }

    /**
     * Saves token to localStorage
     */
    private saveTokenToStorage(token: string): void {
        if (typeof window !== 'undefined') {
            localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, token);
        }
    }

    /**
     * Clears token from localStorage
     */
    private clearTokenFromStorage(): void {
        if (typeof window !== 'undefined') {
            localStorage.removeItem(ACCESS_TOKEN_STORAGE_KEY);
        }
    }

    private isTokenExpiringSoon(token: string): boolean {
        const expiresAt = this.loadTokenExpirationFromStorage() ?? this.getJwtExpiration(token);
        return expiresAt !== null && expiresAt - Date.now() <= this.TOKEN_REFRESH_BUFFER_MS;
    }

    private getJwtExpiration(token: string): number | null {
        try {
            const payload = token.split('.')[1];
            if (!payload || typeof window === 'undefined') {
                return null;
            }

            const json = atob(payload.replace(/-/g, '+').replace(/_/g, '/'));
            const expiresAt = JSON.parse(json).exp;
            return typeof expiresAt === 'number' ? expiresAt * 1_000 : null;
        } catch {
            return null;
        }
    }

    private loadTokenExpirationFromStorage(): number | null {
        if (typeof window === 'undefined') {
            return null;
        }

        const expiresAt = localStorage.getItem(ACCESS_TOKEN_EXPIRATION_STORAGE_KEY);
        const timestamp = expiresAt ? Date.parse(expiresAt) : NaN;
        return Number.isNaN(timestamp) ? null : timestamp;
    }

    private saveTokenExpirationToStorage(expiresAt: string): void {
        if (typeof window !== 'undefined') {
            localStorage.setItem(ACCESS_TOKEN_EXPIRATION_STORAGE_KEY, expiresAt);
        }
    }

    private clearTokenExpirationFromStorage(): void {
        if (typeof window !== 'undefined') {
            localStorage.removeItem(ACCESS_TOKEN_EXPIRATION_STORAGE_KEY);
        }
    }

    private loadRefreshTokenFromStorage(): string | null {
        return typeof window === 'undefined' ? null : localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY);
    }

    private saveRefreshTokenToStorage(token: string): void {
        if (typeof window !== 'undefined') {
            localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, token);
        }
    }

    private clearRefreshTokenFromStorage(): void {
        if (typeof window !== 'undefined') {
            localStorage.removeItem(REFRESH_TOKEN_STORAGE_KEY);
        }
    }

    private isSafeReturnUrl(returnUrl?: string): returnUrl is string {
        return !!returnUrl
            && returnUrl.startsWith('/')
            && !returnUrl.startsWith('//')
            && !returnUrl.includes('\\');
    }

    clearLocalAuth(): void {
        this.authGeneration++;
        this.clearTokenFromStorage();
        this.clearTokenExpirationFromStorage();
        this.clearRefreshTokenFromStorage();
        this.authStore.clearAuth();
        this.appStore.clearState();
        this.guildsCache = null;
        this.guildsRequest$ = null;
        this.guildsRefreshAvailableAt = 0;
        this.operatorAccessRequestToken = null;
    }
}
