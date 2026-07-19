import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { AuthStore } from '../../store/auth.store';
import { AppStore } from '../../store/app.store';
import { DashboardData, GuildService } from '../../services/guild.service';
import { TranslocoPipe } from '@jsverse/transloco';
import { LocaleService } from '../../i18n/locale.service';
import { DomainValueService } from '../../i18n/domain-value.service';
import { ApiErrorService } from '../../services/api-error.service';
import { finalize } from 'rxjs';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterLink, TranslocoPipe],
  template: `
    <div class="dashboard">
      <div class="dashboard-header">
        <div class="welcome-section">
          <h1>{{ 'dashboard.welcome' | transloco: { name: authStore.user()?.username } }}</h1>
          <p *ngIf="data()">{{ 'dashboard.subtitle' | transloco: { guild: data()!.guildName } }}</p>
        </div>
      </div>

      @if (error()) {
        <div class="load-error" role="alert"><span>{{ error() }}</span><button type="button" (click)="loadDashboardData()">{{ 'common.retry' | transloco }}</button></div>
      }

      <div class="stats-grid">
        <div class="stat-card">
          <div class="stat-icon">
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <rect x="2" y="3" width="20" height="4" rx="1"/>
              <rect x="2" y="9" width="20" height="4" rx="1"/>
              <rect x="2" y="15" width="20" height="4" rx="1"/>
            </svg>
          </div>
          <div class="stat-info">
            <h3>{{ 'dashboard.members' | transloco }}</h3>
            <p class="stat-number">{{ format(data()?.memberCount) }}</p>
            <p class="stat-label">{{ 'dashboard.memberStats' | transloco: { members: format(data()?.memberCount), bots: format(data()?.botCount) } }}</p>
          </div>
        </div>

        <div class="stat-card">
          <div class="stat-icon">
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/>
              <circle cx="9" cy="7" r="4"/>
              <path d="M22 21v-2a4 4 0 0 0-3-3.87"/>
              <path d="M16 3.13a4 4 0 0 1 0 7.75"/>
            </svg>
          </div>
          <div class="stat-info">
            <h3>{{ 'dashboard.voiceActive' | transloco }}</h3>
            <p class="stat-number">{{ format(data()?.activeVoiceMembers) }}</p>
            <p class="stat-label">{{ 'dashboard.inVoice' | transloco }}</p>
          </div>
        </div>

        <div class="stat-card">
          <div class="stat-icon">
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M12 2L2 7l10 5 10-5-10-5z"/>
              <path d="m2 17 10 5 10-5"/>
              <path d="m2 12 10 5 10-5"/>
            </svg>
          </div>
          <div class="stat-info">
            <h3>{{ 'dashboard.xpAwarded' | transloco }}</h3>
            <p class="stat-number">{{ format(data()?.stats?.xpAwarded) }}</p>
            <p class="stat-label">{{ 'dashboard.accumulatedXp' | transloco }}</p>
          </div>
        </div>

        <div class="stat-card">
          <div class="stat-icon">
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M22 12h-4l-3 9L9 3l-3 9H2"/>
            </svg>
          </div>
          <div class="stat-info">
            <h3>{{ 'dashboard.temporaryVcs' | transloco }}</h3>
            <p class="stat-number">{{ format(data()?.activeTemporaryChannels) }}</p>
            <p class="stat-label">{{ 'dashboard.currentlyActive' | transloco }}</p>
          </div>
        </div>
      </div>

      <div class="dashboard-content">
        <div class="content-grid">
          <div class="card recent-activity">
            <div class="card-header">
              <h3>{{ 'dashboard.xpLeaderboard' | transloco }}</h3>
              <a class="view-all-btn" [routerLink]="['/rankings', data()?.leaderboardAlias]">{{ 'common.viewAll' | transloco }}</a>
            </div>
            <div class="activity-list">
              <div class="activity-item" *ngFor="let member of data()?.leaderboard; let index = index">
                <div class="activity-icon">
                  <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M12 2L2 7l10 5 10-5-10-5z"/>
                    <path d="m2 17 10 5 10-5"/>
                    <path d="m2 12 10 5 10-5"/>
                  </svg>
                </div>
                <div class="activity-content">
                  <p><strong>#{{ index + 1 }} {{ member.displayName }}</strong> · {{ 'common.levelValue' | transloco: { value: format(member.level) } }}</p>
                  <span class="activity-time">{{ 'dashboard.memberXpVoice' | transloco: { xp: format(member.totalXp), seconds: format(member.voiceSeconds) } }}</span>
                </div>
              </div>
            </div>
          </div>

          <div class="card quick-actions">
            <div class="card-header">
              <h3>{{ 'dashboard.quickActions' | transloco }}</h3>
            </div>
            <div class="action-grid">
              <a class="action-btn" routerLink="/xp">
                <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/>
                </svg>
                <span>{{ 'dashboard.configureXp' | transloco }}</span>
              </a>
              
              <a class="action-btn" routerLink="/vc-hubs">
                <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <line x1="12" y1="1" x2="12" y2="23"/>
                  <path d="M17 5H9.5a3.5 3.5 0 0 0 0 7h5a3.5 3.5 0 0 1 0 7H6"/>
                </svg>
                <span>{{ 'dashboard.setupHub' | transloco }}</span>
              </a>
              
              <div class="action-btn">
                <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
                  <polyline points="14,2 14,8 20,8"/>
                  <line x1="16" y1="13" x2="8" y2="13"/>
                  <line x1="16" y1="17" x2="8" y2="17"/>
                  <polyline points="10,9 9,9 8,9"/>
                </svg>
                <span>{{ 'dashboard.watchdog' | transloco: { state: watchdogState(data()?.watchdog?.state) } }}</span>
              </div>
              
              <div class="action-btn">
                <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <circle cx="12" cy="12" r="3"/>
                  <path d="M12 1v6m0 6v6m11-7h-6m-6 0H1"/>
                </svg>
                <span>{{ 'dashboard.messagesReactions' | transloco: { messages: format(data()?.stats?.messages ?? 0), reactions: format(data()?.stats?.reactions ?? 0) } }}</span>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  `,
  styleUrls: ['./dashboard.component.scss']
})
export class DashboardComponent implements OnInit {
  public readonly authStore = inject(AuthStore);
  public readonly appStore = inject(AppStore);
  private readonly guildService = inject(GuildService);
  private readonly locale = inject(LocaleService);
  private readonly domain = inject(DomainValueService);
  private readonly apiErrors = inject(ApiErrorService);
  public readonly data = signal<DashboardData | null>(null);
  public readonly loading = signal(false);
  public readonly error = signal('');

  ngOnInit(): void {
    this.loadDashboardData();
  }

  loadDashboardData(): void {
    const guild = this.appStore.selectedGuild();
    if (!guild) return;
    this.loading.set(true);
    this.error.set('');
    this.guildService.dashboard(guild.id).pipe(finalize(() => this.loading.set(false))).subscribe({
      next: data => this.data.set(data),
      error: error => {
        this.data.set(null);
        this.error.set(this.apiErrors.resolve(error, 'errors.dashboardLoad').message);
      }
    });
  }

  format(value: string | number | null | undefined): string { return value == null ? '-' : this.locale.number(value); }
  watchdogState(value: string | null | undefined): string { return this.domain.watchdogState(value); }
}
