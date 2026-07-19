import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../services/auth.service';
import { AuthStore } from '../../store/auth.store';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiErrorService } from '../../services/api-error.service';

@Component({
  selector: 'app-auth-callback',
  standalone: true,
  imports: [CommonModule, TranslocoPipe],
  template: `
    <div class="callback-container">
      <div class="callback-content">
        <div class="spinner">
          <svg width="50" height="50" viewBox="0 0 50 50">
            <circle cx="25" cy="25" r="20" fill="none" stroke="#3498db" stroke-width="2" stroke-linecap="round" stroke-dasharray="31.416" stroke-dashoffset="31.416">
              <animate attributeName="stroke-dasharray" dur="2s" values="0 31.416;15.708 15.708;0 31.416" repeatCount="indefinite"/>
              <animate attributeName="stroke-dashoffset" dur="2s" values="0;-15.708;-31.416" repeatCount="indefinite"/>
            </circle>
          </svg>
        </div>
        
        <h2>{{ 'authCallback.processing' | transloco }}</h2>
        <p>{{ 'authCallback.wait' | transloco }}</p>
        
        <div *ngIf="authStore.hasError()" class="error-message">
          <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <circle cx="12" cy="12" r="10"/>
            <line x1="15" y1="9" x2="9" y2="15"/>
            <line x1="9" y1="9" x2="15" y2="15"/>
          </svg>
          <div>
            <h3>{{ 'authCallback.failed' | transloco }}</h3>
            <p>{{ authStore.error() }}</p>
            <button class="retry-btn" (click)="retry()">{{ 'common.retry' | transloco }}</button>
          </div>
        </div>
      </div>
    </div>
  `,
  styleUrls: ['./auth-callback.component.scss']
})
export class AuthCallbackComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly authService = inject(AuthService);
  public readonly authStore = inject(AuthStore);
  private readonly i18n = inject(TranslocoService);
  private readonly apiErrors = inject(ApiErrorService);

  ngOnInit(): void {
    this.handleAuthCallback();
  }

  private handleAuthCallback(): void {
    const token = this.route.snapshot.queryParams['token'];
    const refreshToken = this.route.snapshot.queryParams['refresh_token'];
    const errorKey = this.route.snapshot.queryParams['errorKey'];
    const errorMessage = this.route.snapshot.queryParams['message'];

    const return_url = this.route.snapshot.queryParams['return_url'] || '/dashboard';
    console.log('Return URL:', return_url, this.route.snapshot.queryParams['return_url']);

    if (errorKey) {
      this.authService.clearLocalAuth();
      this.authStore.setError(this.apiErrors.resolve({ error: { errorKey, message: errorMessage } }, 'errors.authFailed').message);
      return;
    }

    if (!token) {
      this.authService.clearLocalAuth();
      this.authStore.setError(this.i18n.translate('errors.authTokenMissing'));
      return;
    }

    if (!refreshToken) {
      this.authService.clearLocalAuth();
      this.authStore.setError(this.i18n.translate('errors.authRefreshTokenMissing'));
      return;
    }

    this.authService.handleTokenCallback(token, refreshToken).subscribe({
      next: (success) => {
        if (success) {
          this.router.navigate([return_url]);
        }
      },
      error: (error) => {
        console.error('Token callback error:', error);
        this.authStore.setError(this.i18n.translate('errors.authFailed'));
      }
    });
  }

  retry(): void {
    this.router.navigate(['/login']);
  }
}
