import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { Observable, of } from 'rxjs';
import { catchError, tap, map } from 'rxjs/operators';
import { AuthStore, User } from '../store/auth.store';
import { AppStore } from '../store/app.store';
import { environment } from '../../environments/environment';

export interface BackendTokenResponse {
    token: string;
    user: User;
    expiresAt: string;
}

export interface Guild {
    id: string;
    name: string;
    icon: string | null;
    owner: boolean;
    permissions: string;
    features: string[];
    botInstalled: boolean;
    inviteUrl: string;
}

@Injectable({
    providedIn: 'root'
})
export class AuthService {
    private readonly http = inject(HttpClient);
    private readonly router = inject(Router);
    private readonly authStore = inject(AuthStore);
    private readonly appStore = inject(AppStore);

    private readonly DISCORD_CLIENT_ID = environment.discordClientId;
    private readonly DISCORD_REDIRECT_URI = environment.discordRedirectUri; // Backend URL
    private readonly API_BASE_URL = environment.apiBaseUrl;

    constructor() {
        this.loadTokenFromStorage();
    }

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
                this.authStore.setError('Fehler beim Starten der Anmeldung.');
                console.error('Login URL fetch error:', err);
            }
        });
    }

    /**
     * Handles callback with backend token from query parameter
     */
    handleTokenCallback(token: string): Observable<boolean> {
        this.authStore.setLoading(true);
        this.authStore.setError(null);

        this.authStore.setToken(token);
        return this.validateBackendToken().pipe(
            tap(response => {
                this.saveTokenToStorage(response.token);
                this.authStore.setToken(response.token);
                this.authStore.setUser(response.user);
                this.authStore.setLoading(false);
            }),
            map(() => true),
            catchError(error => {
                console.error('Token validation error:', error);
                this.authStore.setToken('');
                this.authStore.setError('Fehler beim Anmelden. Bitte versuchen Sie es erneut.');
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
        const currentToken = this.authStore.token();
        if (!currentToken) {
            return of(false);
        }

        return this.http.post<BackendTokenResponse>(`${this.API_BASE_URL}/auth/refresh`, {
            token: currentToken
        }).pipe(
            tap(response => {
                this.saveTokenToStorage(response.token);
                this.authStore.setToken(response.token);
                this.authStore.setUser(response.user);
            }),
            map(() => true),
            catchError(error => {
                console.error('Token refresh failed:', error);
                this.logout();
                return of(false);
            })
        );
    }

    /**
     * Logs out the user
     */
    logout(): void {
        const token = this.authStore.token();

        // Optional: Notify backend about logout
        if (token) {
            this.http.post(`${this.API_BASE_URL}/auth/logout`, { token }).subscribe({
                error: (error) => console.warn('Logout notification failed:', error)
            });
        }

        this.clearTokenFromStorage();
        this.authStore.clearAuth();
        this.appStore.clearState();
        this.router.navigate(['/login']);
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
                }
                return of([]);
            })
        );
    }

    /**
     * Loads token from localStorage and validates it
     */
    private loadTokenFromStorage(): void {
        if (typeof window !== 'undefined') {
            const token = localStorage.getItem('rankoon_token');

            if (token) {
                this.authStore.setToken(token);
                // Validate token and get user info
                this.getCurrentUser().subscribe();
            }
        }
    }

    /**
     * Saves token to localStorage
     */
    private saveTokenToStorage(token: string): void {
        if (typeof window !== 'undefined') {
            localStorage.setItem('rankoon_token', token);
        }
    }

    /**
     * Clears token from localStorage
     */
    private clearTokenFromStorage(): void {
        if (typeof window !== 'undefined') {
            localStorage.removeItem('rankoon_token');
        }
    }
}
