import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { catchError, tap, map } from 'rxjs/operators';
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

@Injectable({
    providedIn: 'root'
})
export class AuthService {
    private readonly http = inject(HttpClient);
    private readonly router = inject(Router);
    private readonly authStore = inject(AuthStore);
    private readonly appStore = inject(AppStore);
    private readonly apiErrors = inject(ApiErrorService);

    private readonly DISCORD_CLIENT_ID = environment.discordClientId;
    private readonly DISCORD_REDIRECT_URI = environment.discordRedirectUri; // Backend URL
    private readonly API_BASE_URL = environment.apiBaseUrl;

    /**
     * Initiates Discord OAuth2 login flow - gets login URL from backend
     */
    login(returnUrl?: string): void {
        const url = new URL(`${this.API_BASE_URL}/auth/login`, window.location.origin);
        if (returnUrl) {
            url.searchParams.set('returnUrl', returnUrl);
        }
        this.http.get<{ loginUrl: string }>(url.toString()).subscribe({
            next: (res) => {
                if (res?.loginUrl) {
                    console.log(res.loginUrl)
                    window.location.href = res.loginUrl;
                }
            },
            error: (err) => {
                this.authStore.setError(this.apiErrors.resolve(err, 'errors.loginStart').message);
                console.error('Login URL fetch error:', err);
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
                this.saveRefreshTokenToStorage(refreshToken);
                this.authStore.setToken(response.token);
                this.authStore.setUser(response.user);
                this.authStore.setLoading(false);
            }),
            map(() => true),
            catchError(error => {
                console.error('Token validation error:', error);
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
        const refreshToken = this.loadRefreshTokenFromStorage();
        if (!refreshToken) {
            this.clearLocalAuth();
            return of(false);
        }

        return this.http.post<BackendRefreshResponse>(`${this.API_BASE_URL}/auth/refresh`, {
            refreshToken
        }).pipe(
            tap(response => {
                this.saveTokenToStorage(response.accessToken);
                this.saveRefreshTokenToStorage(response.refreshToken);
                this.authStore.setToken(response.accessToken);
                this.authStore.setUser(response.user);
            }),
            map(() => true),
            catchError(error => {
                console.error('Token refresh failed:', error);
                this.clearLocalAuth();
                void this.router.navigate(['/login']);
                return of(false);
            })
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

    /**
     * Gets user's Discord guilds from backend
     */
    getUserGuilds(): Observable<Guild[]> {
        const token = this.authStore.token();
        if (!token) {
            return of([]);
        }

        return this.http.get<Guild[]>(`${this.API_BASE_URL}/auth/guilds`, {
            headers: {
                'Authorization': `Bearer ${token}`
            }
        }).pipe(
            catchError(error => {
                console.error('Failed to fetch user guilds:', error);
                if (error.status === 401) {
                    this.logout();
                    return of([]);
                }
                return throwError(() => error);
            })
        );
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

    clearLocalAuth(): void {
        this.clearTokenFromStorage();
        this.clearRefreshTokenFromStorage();
        this.authStore.clearAuth();
        this.appStore.clearState();
    }
}
