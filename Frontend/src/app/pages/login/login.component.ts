import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AuthService } from '../../services/auth.service';
import { AuthStore } from '../../store/auth.store';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [CommonModule, RouterLink, TranslocoPipe],
  template: `
    <div class="login-page">
      <a class="back-link" routerLink="/">
        <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M19 12H5m6-6-6 6 6 6"/></svg>
        {{ 'login.back' | transloco }}
      </a>
      <section class="login-hero" aria-labelledby="login-title">
        <div class="login-copy">
          <div class="login-copy-text">
          <p class="overline">RANKOON / CONTROL DECK</p>
          <h1 id="login-title">{{ 'login.title' | transloco }}</h1>
          <p class="intro">{{ 'login.intro' | transloco }}</p>
          </div>
          <div class="login-features">
            <div class="feature">
              <svg aria-hidden="true" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/>
              </svg>
              <span>{{ 'login.secure' | transloco }}</span>
            </div>
            <div class="feature">
              <svg aria-hidden="true" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <path d="M12 2L2 7l10 5 10-5-10-5z"/>
                <path d="m2 17 10 5 10-5"/>
                <path d="m2 12 10 5 10-5"/>
              </svg>
              <span>{{ 'login.control' | transloco }}</span>
            </div>
            <div class="feature">
              <svg aria-hidden="true" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
                <polyline points="14,2 14,8 20,8"/>
                <line x1="16" y1="13" x2="8" y2="13"/>
                <line x1="16" y1="17" x2="8" y2="17"/>
                <polyline points="10,9 9,9 8,9"/>
              </svg>
              <span>{{ 'login.analytics' | transloco }}</span>
            </div>
          </div>
        </div>
        <section class="login-deck" aria-labelledby="sign-in-title">
          <div class="deck-bar"><span class="deck-label">{{ 'app.dashboard' | transloco }}</span><span>RANKOON / LOGIN</span></div>
          <div class="deck-content">
            <div class="deck-brand"><img src="/rankoon/icon/favicon.ico" alt="" class="logo"><span>Rankoon<span class="brand-caption">CONTROL DECK</span></span></div>
            <h2 id="sign-in-title">{{ 'login.panelTitle' | transloco }}</h2>
            <p id="login-description" class="login-description">{{ 'login.continueHint' | transloco }}</p>
            <div *ngIf="authStore.hasError()" class="error-message" role="alert">
              <p>{{ authStore.error() }}</p>
              <p>{{ 'login.retryHint' | transloco }}</p>
            </div>

            <button
              type="button"
              class="rk-button rk-button--discord discord-login-btn"
              [disabled]="authStore.isLoading()"
              [attr.aria-busy]="authStore.isLoading()"
              aria-describedby="login-description"
              (click)="login()"
            >
              <span class="btn-content">
                <svg aria-hidden="true" width="24" height="24" viewBox="0 0 24 24" class="discord-icon">
                  <path fill="currentColor" d="M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419-.0003 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9554 2.4189-2.1568 2.4189Z"/>
                </svg>
                <span aria-live="polite">{{ (authStore.isLoading() ? 'login.signingIn' : 'login.signIn') | transloco }}</span>
              </span>
            </button>
            <div class="auth-note">
              <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="5" y="10" width="14" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/></svg>
              <p>{{ 'login.authNote' | transloco }}</p>
            </div>
          </div>
        </section>
      </section>
      <footer class="legal-footer">
        <span>{{ 'login.copyright' | transloco }}</span>
        <nav [attr.aria-label]="'login.legalAria' | transloco"><a routerLink="/tos">{{ 'landing.terms' | transloco }}</a><a routerLink="/privacy">{{ 'landing.privacy' | transloco }}</a></nav>
      </footer>
    </div>
  `,
  styleUrls: ['./login.component.scss']
})
export class LoginComponent {
  private readonly authService = inject(AuthService);
  public readonly authStore = inject(AuthStore);
  private readonly route = inject(ActivatedRoute);

  login(): void {
    const returnUrl = this.route.snapshot.queryParams['returnUrl'] || '/dashboard';
    this.authService.login(returnUrl);
  }
}
