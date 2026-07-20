import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

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
              <a class="button button-primary" routerLink="/login">{{ 'landing.start' | transloco }}<svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M5 12h14"/><path d="m13 6 6 6-6 6"/></svg></a>
              <a class="button button-secondary" href="#features">{{ 'landing.explore' | transloco }}</a>
            </div>
            <p class="login-hint">{{ 'landing.loginHint' | transloco }}</p>
          </div>

          <div class="command-deck" aria-label="Rankoon feature preview">
            <div class="deck-bar"><span class="deck-live"><i aria-hidden="true"></i>{{ 'landing.previewLive' | transloco }}</span><span>OVERVIEW</span></div>
            <div class="deck-main">
              <div class="deck-title"><span class="deck-mark">R</span><div><strong>{{ 'landing.previewServer' | transloco }}</strong><small>{{ 'landing.previewStatus' | transloco }}</small></div></div>
              <div class="metric-grid">
                <div class="metric"><span>{{ 'landing.metricMembers' | transloco }}</span><strong>1,248</strong><small>+12.4%</small></div>
                <div class="metric"><span>{{ 'landing.metricVoice' | transloco }}</span><strong>84</strong><small>LIVE</small></div>
                <div class="metric"><span>{{ 'landing.metricXp' | transloco }}</span><strong>47.2k</strong><small>+8.1%</small></div>
              </div>
              <div class="activity-panel">
                <div class="panel-heading"><span>{{ 'landing.previewActivity' | transloco }}</span><span class="pulse" aria-hidden="true"></span></div>
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
            <div class="sparkline" aria-hidden="true"><i></i><i></i><i></i><i></i><i></i><i></i><i></i></div>
          </article>
          <article class="feature-card">
            <div class="feature-icon"><svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 2a3 3 0 0 0-3 3v7a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3Z"/><path d="M19 10v2a7 7 0 0 1-14 0v-2"/><path d="M12 19v3"/></svg></div>
            <h3>{{ 'landing.voiceTitle' | transloco }}</h3><p>{{ 'landing.voiceText' | transloco }}</p>
          </article>
          <article class="feature-card">
            <div class="feature-icon"><svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z"/><path d="M9 12l2 2 4-4"/></svg></div>
            <h3>{{ 'landing.logsTitle' | transloco }}</h3><p>{{ 'landing.logsText' | transloco }}</p>
          </article>
        </div>
      </section>

      <section class="closing" aria-labelledby="closing-title">
        <div><p class="overline">{{ 'landing.closingOverline' | transloco }}</p><h2 id="closing-title">{{ 'landing.closingTitle' | transloco }}</h2></div>
        <a class="button button-primary" routerLink="/login">{{ 'landing.closingAction' | transloco }}<svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M5 12h14"/><path d="m13 6 6 6-6 6"/></svg></a>
      </section>
      <footer class="legal-footer">
        <span>Rankoon</span>
        <nav aria-label="Rechtliche Hinweise"><a routerLink="/tos">{{ 'landing.terms' | transloco }}</a><a routerLink="/privacy">{{ 'landing.privacy' | transloco }}</a></nav>
      </footer>
    </div>
  `,
  styleUrl: './landing.component.scss',
})
export class LandingComponent {}
