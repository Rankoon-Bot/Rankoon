import { Injectable, signal, computed } from '@angular/core';

export interface User {
    id: string;
    discordId: string;
    username: string;
    displayName: string;
    email?: string;
    avatar: string;
    verified?: boolean;
}

export interface AuthState {
  user: User | null;
  token: string | null;
  isLoading: boolean;
  error: string | null;
}

@Injectable({
  providedIn: 'root'
})
export class AuthStore {
  // Private writable signals
  private readonly _user = signal<User | null>(null);
  private readonly _token = signal<string | null>(null);
  private readonly _isLoading = signal<boolean>(false);
  private readonly _error = signal<string | null>(null);

  // Public readonly computed signals
  readonly user = this._user.asReadonly();
  readonly token = this._token.asReadonly();
  readonly isLoading = this._isLoading.asReadonly();
  readonly error = this._error.asReadonly();

  // Computed properties
  readonly isAuthenticated = computed(() => this._user() !== null && this._token() !== null);
  readonly hasError = computed(() => this._error() !== null);

  // Actions
  setUser(user: User | null): void {
    this._user.set(user);
  }

  setToken(token: string | null): void {
    this._token.set(token);
  }

  setLoading(isLoading: boolean): void {
    this._isLoading.set(isLoading);
  }

  setError(error: string | null): void {
    this._error.set(error);
  }

  setAuthData(user: User, token: string): void {
    this._user.set(user);
    this._token.set(token);
    this._error.set(null);
  }

  clearAuth(): void {
    this._user.set(null);
    this._token.set(null);
    this._isLoading.set(false);
    this._error.set(null);
  }

  // State getter for debugging or serialization
  getState(): AuthState {
    return {
      user: this._user(),
      token: this._token(),
      isLoading: this._isLoading(),
      error: this._error()
    };
  }
}
