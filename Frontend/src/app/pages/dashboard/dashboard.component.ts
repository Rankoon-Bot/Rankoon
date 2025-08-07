import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { AuthStore } from '../../store/auth.store';
import { AppStore } from '../../store/app.store';
import { DashboardData, GuildService } from '../../services/guild.service';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterLink],
  template: `
    <div class="dashboard">
      <div class="dashboard-header">
        <div class="welcome-section">
          <h1>Willkommen zurück, {{ authStore.user()?.username }}!</h1>
          <p *ngIf="data()">{{ data()!.guildName }}: echte Aktivitaeten und Modulstatus</p>
        </div>
      </div>

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
            <h3>Mitglieder</h3>
            <p class="stat-number">{{ data()?.memberCount ?? '-' }}</p>
            <p class="stat-label">Servermitglieder</p>
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
            <h3>Voice aktiv</h3>
            <p class="stat-number">{{ data()?.activeVoiceMembers ?? '-' }}</p>
            <p class="stat-label">Mitglieder im Voice</p>
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
            <h3>XP vergeben</h3>
            <p class="stat-number">{{ data()?.stats?.xpAwarded ?? '-' }}</p>
            <p class="stat-label">Akkumulierte XP</p>
          </div>
        </div>

        <div class="stat-card">
          <div class="stat-icon">
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M22 12h-4l-3 9L9 3l-3 9H2"/>
            </svg>
          </div>
          <div class="stat-info">
            <h3>Temporäre VCs</h3>
            <p class="stat-number">{{ data()?.activeTemporaryChannels ?? '-' }}</p>
            <p class="stat-label">Aktuell aktiv</p>
          </div>
        </div>
      </div>

      <div class="dashboard-content">
        <div class="content-grid">
          <div class="card recent-activity">
            <div class="card-header">
              <h3>XP-Rangliste</h3>
              <a class="view-all-btn" routerLink="/xp">XP konfigurieren</a>
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
                  <p><strong>#{{ index + 1 }} {{ member.displayName }}</strong> · Level {{ member.level }}</p>
                  <span class="activity-time">{{ member.totalXp }} XP · {{ member.voiceSeconds }} Voice-Sekunden</span>
                </div>
              </div>
            </div>
          </div>

          <div class="card quick-actions">
            <div class="card-header">
              <h3>Schnellaktionen</h3>
            </div>
            <div class="action-grid">
              <a class="action-btn" routerLink="/xp">
                <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/>
                </svg>
                <span>XP & Level konfigurieren</span>
              </a>
              
              <a class="action-btn" routerLink="/vc-hubs">
                <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <line x1="12" y1="1" x2="12" y2="23"/>
                  <path d="M17 5H9.5a3.5 3.5 0 0 0 0 7h5a3.5 3.5 0 0 1 0 7H6"/>
                </svg>
                <span>VC-Hub einrichten</span>
              </a>
              
              <div class="action-btn">
                <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
                  <polyline points="14,2 14,8 20,8"/>
                  <line x1="16" y1="13" x2="8" y2="13"/>
                  <line x1="16" y1="17" x2="8" y2="17"/>
                  <polyline points="10,9 9,9 8,9"/>
                </svg>
                <span>Watchdog: {{ data()?.watchdog?.state ?? 'unbekannt' }}</span>
              </div>
              
              <div class="action-btn">
                <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <circle cx="12" cy="12" r="3"/>
                  <path d="M12 1v6m0 6v6m11-7h-6m-6 0H1"/>
                </svg>
                <span>{{ data()?.stats?.messages ?? 0 }} Nachrichten · {{ data()?.stats?.reactions ?? 0 }} Reaktionen</span>
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
  public readonly data = signal<DashboardData | null>(null);

  ngOnInit(): void {
    this.loadDashboardData();
  }

  private loadDashboardData(): void {
    const guild = this.appStore.selectedGuild();
    if (guild) this.guildService.dashboard(guild.id).subscribe({ next: data => this.data.set(data), error: () => this.data.set(null) });
  }
}
