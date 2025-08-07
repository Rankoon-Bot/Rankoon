import { Component, inject, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AuthStore } from '../../store/auth.store';
import { AppStore } from '../../store/app.store';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="dashboard">
      <div class="dashboard-header">
        <div class="welcome-section">
          <h1>Willkommen zurück, {{ authStore.user()?.username }}!</h1>
          <p>Hier ist eine Übersicht über deine Bot-Aktivitäten</p>
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
            <h3>Server</h3>
            <p class="stat-number">{{ appStore.guilds().length }}</p>
            <p class="stat-label">Aktive Server</p>
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
            <h3>Benutzer</h3>
            <p class="stat-number">1.234</p>
            <p class="stat-label">Aktive Nutzer</p>
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
            <h3>Commands</h3>
            <p class="stat-number">5.678</p>
            <p class="stat-label">Heute ausgeführt</p>
          </div>
        </div>

        <div class="stat-card">
          <div class="stat-icon">
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M22 12h-4l-3 9L9 3l-3 9H2"/>
            </svg>
          </div>
          <div class="stat-info">
            <h3>Uptime</h3>
            <p class="stat-number">99.9%</p>
            <p class="stat-label">Verfügbarkeit</p>
          </div>
        </div>
      </div>

      <div class="dashboard-content">
        <div class="content-grid">
          <div class="card recent-activity">
            <div class="card-header">
              <h3>Letzte Aktivitäten</h3>
              <button class="view-all-btn">Alle anzeigen</button>
            </div>
            <div class="activity-list">
              <div class="activity-item">
                <div class="activity-icon">
                  <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M12 2L2 7l10 5 10-5-10-5z"/>
                    <path d="m2 17 10 5 10-5"/>
                    <path d="m2 12 10 5 10-5"/>
                  </svg>
                </div>
                <div class="activity-content">
                  <p><strong>!help</strong> Command wurde ausgeführt</p>
                  <span class="activity-time">vor 2 Minuten</span>
                </div>
              </div>
              
              <div class="activity-item">
                <div class="activity-icon">
                  <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/>
                    <circle cx="9" cy="7" r="4"/>
                    <path d="M22 21v-2a4 4 0 0 0-3-3.87"/>
                    <path d="M16 3.13a4 4 0 0 1 0 7.75"/>
                  </svg>
                </div>
                <div class="activity-content">
                  <p>Neuer Benutzer <strong>Max#1234</strong> beigetreten</p>
                  <span class="activity-time">vor 15 Minuten</span>
                </div>
              </div>
              
              <div class="activity-item">
                <div class="activity-icon">
                  <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/>
                  </svg>
                </div>
                <div class="activity-content">
                  <p>Automod hat eine Nachricht <strong>gelöscht</strong></p>
                  <span class="activity-time">vor 1 Stunde</span>
                </div>
              </div>
            </div>
          </div>

          <div class="card quick-actions">
            <div class="card-header">
              <h3>Schnellaktionen</h3>
            </div>
            <div class="action-grid">
              <button class="action-btn">
                <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/>
                </svg>
                <span>Automod konfigurieren</span>
              </button>
              
              <button class="action-btn">
                <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <line x1="12" y1="1" x2="12" y2="23"/>
                  <path d="M17 5H9.5a3.5 3.5 0 0 0 0 7h5a3.5 3.5 0 0 1 0 7H6"/>
                </svg>
                <span>Economy einrichten</span>
              </button>
              
              <button class="action-btn">
                <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
                  <polyline points="14,2 14,8 20,8"/>
                  <line x1="16" y1="13" x2="8" y2="13"/>
                  <line x1="16" y1="17" x2="8" y2="17"/>
                  <polyline points="10,9 9,9 8,9"/>
                </svg>
                <span>Logs anzeigen</span>
              </button>
              
              <button class="action-btn">
                <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <circle cx="12" cy="12" r="3"/>
                  <path d="M12 1v6m0 6v6m11-7h-6m-6 0H1"/>
                </svg>
                <span>Einstellungen</span>
              </button>
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

  ngOnInit(): void {
    // Load initial dashboard data
    this.loadDashboardData();
  }

  private loadDashboardData(): void {
    // TODO: Load guilds and other dashboard data from API
    console.log('Loading dashboard data...');
  }
}
