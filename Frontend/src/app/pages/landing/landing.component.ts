import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { AuthService } from '../../services/auth.service';

@Component({
  selector: 'app-landing',
  standalone: true,
  imports: [RouterLink, TranslocoPipe],
  template: `
    <div class="landing">
      <section class="hero" aria-labelledby="landing-title">
        <div class="eyebrow"><span class="status-dot" aria-hidden="true"></span>{{ 'landing.eyebrow' | transloco }}</div>
        <div class="hero-grid">
          <div class="hero-copy">
            <p class="overline">RANKOON / CONTROL DECK</p>
            <h1 id="landing-title">{{ 'landing.title' | transloco }}</h1>
            <p class="intro">{{ 'landing.intro' | transloco }}</p>
            <div class="hero-actions">
              <a class="button button-primary" [href]="botInviteUrl()" target="_blank" rel="noopener noreferrer">{{ 'landing.start' | transloco }}<svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M5 12h14"/><path d="m13 6 6 6-6 6"/></svg></a>
              <a class="button button-secondary" routerLink="/login">{{ 'landing.loginAction' | transloco }}</a>
            </div>
            <p class="feature-link"><a href="#features">{{ 'landing.explore' | transloco }}</a></p>
            <aside class="hosting-note">
              <div><strong>{{ 'landing.hostedTitle' | transloco }}</strong><p>{{ 'landing.hostedText' | transloco }}</p></div>
              <a href="https://github.com/Rankoon-Bot/Rankoon#self-hosting" target="_blank" rel="noopener noreferrer">{{ 'landing.selfHostAction' | transloco }}<span aria-hidden="true">↗</span></a>
            </aside>
          </div>

          <div class="command-deck" [attr.aria-label]="'landing.previewAria' | transloco">
            <div class="deck-bar"><span class="deck-demo">{{ 'landing.previewLive' | transloco }}</span><span>RANKOON / XP</span></div>
            <div class="deck-main">
              <div class="deck-title"><span class="deck-mark">R</span><div><strong>{{ 'landing.previewServer' | transloco }}</strong><small>{{ 'landing.previewStatus' | transloco }}</small></div></div>
              <div class="metric-grid">
                <div class="metric"><span>{{ 'landing.metricMembers' | transloco }}</span><strong>420</strong><small>XP</small></div>
                <div class="metric"><span>{{ 'landing.metricVoice' | transloco }}</span><strong>280</strong><small>XP</small></div>
                <div class="metric"><span>{{ 'landing.metricXp' | transloco }}</span><strong>12</strong><small>{{ 'landing.previewLevel' | transloco }}</small></div>
              </div>
              <div class="activity-panel">
                <div class="panel-heading"><span>{{ 'landing.previewActivity' | transloco }}</span></div>
                <div class="activity-row"><span class="rank">01</span><span class="avatar avatar-red">M</span><span>mira.wav</span><b>8,420 XP</b></div>
                <div class="activity-row"><span class="rank">02</span><span class="avatar avatar-blue">K</span><span>kian.exe</span><b>7,916 XP</b></div>
                <div class="activity-row"><span class="rank">03</span><span class="avatar avatar-green">N</span><span>nova</span><b>6,784 XP</b></div>
              </div>
            </div>
          </div>
        </div>
      </section>

      <section id="features" class="features" aria-labelledby="features-title">
        <div class="section-heading"><p class="overline">{{ 'landing.featuresOverline' | transloco }}</p><h2 id="features-title">{{ 'landing.featuresTitle' | transloco }}</h2></div>
        <div class="feature-grid">
          <article class="feature-card feature-card-wide">
            <div class="feature-icon feature-icon-brand"><svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 19V5"/><path d="M4 19h16"/><path d="m7 15 4-5 3 2 5-7"/></svg></div>
            <h3>{{ 'landing.xpTitle' | transloco }}</h3><p>{{ 'landing.xpText' | transloco }}</p>
            <div class="source-tags"><span>{{ 'landing.chatTag' | transloco }}</span><span>{{ 'landing.voiceTag' | transloco }}</span></div>
          </article>
          <article class="feature-card">
            <div class="feature-icon"><svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="m12 3 2.7 5.5 6.1.9-4.4 4.3 1 6.1-5.4-2.9-5.4 2.9 1-6.1-4.4-4.3 6.1-.9L12 3Z"/><path d="M19 3v4M17 5h4"/></svg></div>
            <h3>{{ 'landing.voiceTitle' | transloco }}</h3><p>{{ 'landing.voiceText' | transloco }}</p>
          </article>
          <article class="feature-card">
            <div class="feature-icon"><svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z"/><path d="M14 2v6h6M12 18V11m-3 3 3-3 3 3"/></svg></div>
            <h3>{{ 'landing.logsTitle' | transloco }}</h3><p>{{ 'landing.logsText' | transloco }}</p>
          </article>
        </div>
      </section>

      <section class="closing" aria-labelledby="closing-title">
        <div><p class="overline">{{ 'landing.closingOverline' | transloco }}</p><h2 id="closing-title">{{ 'landing.closingTitle' | transloco }}</h2></div>
        <div class="closing-actions"><a class="button button-primary" [href]="botInviteUrl()" target="_blank" rel="noopener noreferrer">{{ 'landing.closingAction' | transloco }}<svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M5 12h14"/><path d="m13 6 6 6-6 6"/></svg></a><a class="closing-login" routerLink="/login">{{ 'landing.loginAction' | transloco }}</a></div>
      </section>
      <footer class="legal-footer">
        <span>Rankoon</span>
        <nav aria-label="Rechtliche Hinweise"><a routerLink="/tos">{{ 'landing.terms' | transloco }}</a><a routerLink="/privacy">{{ 'landing.privacy' | transloco }}</a></nav>
      </footer>
    </div>
  `,
  styleUrl: './landing.component.scss',
})
export class LandingComponent {
  private readonly authService = inject(AuthService);
  readonly botInviteUrl = signal('https://discord.com/application-directory/1402257269088850112');

  constructor() {
    this.authService.getBotInviteUrl().subscribe({
      next: url => {
        try {
          const invite = new URL(url);
          if (invite.protocol === 'https:' && invite.hostname === 'discord.com') this.botInviteUrl.set(url);
        } catch {
          // Keep the Discord app-directory fallback if the API returns an invalid invite URL.
        }
      },
      error: () => undefined,
    });
  }
}
