import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../services/auth.service';
import { AuthStore } from '../../store/auth.store';

@Component({
  selector: 'app-auth-callback',
  standalone: true,
  imports: [CommonModule],
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
        
        <h2>Anmeldung wird verarbeitet...</h2>
        <p>Bitte warten Sie, während wir Ihre Discord-Authentifizierung abschließen.</p>
        
        <div *ngIf="authStore.hasError()" class="error-message">
          <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <circle cx="12" cy="12" r="10"/>
            <line x1="15" y1="9" x2="9" y2="15"/>
            <line x1="9" y1="9" x2="15" y2="15"/>
          </svg>
          <div>
            <h3>Anmeldung fehlgeschlagen</h3>
            <p>{{ authStore.error() }}</p>
            <button class="retry-btn" (click)="retry()">Erneut versuchen</button>
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

  ngOnInit(): void {
    this.handleAuthCallback();
  }

  private handleAuthCallback(): void {
    const token = this.route.snapshot.queryParams['token'];
    const error = this.route.snapshot.queryParams['error'];

    if (error) {
      this.authStore.setError('Discord Authentifizierung wurde abgebrochen.');
      return;
    }

    if (!token) {
      this.authStore.setError('Kein Authentifizierungstoken erhalten.');
      return;
    }

    this.authService.handleTokenCallback(token).subscribe({
      next: (success) => {
        if (success) {
          // Navigation handled by AuthService
        }
      },
      error: (error) => {
        console.error('Token callback error:', error);
        this.authStore.setError('Fehler bei der Authentifizierung.');
      }
    });
  }

  retry(): void {
    this.router.navigate(['/login']);
  }
}
