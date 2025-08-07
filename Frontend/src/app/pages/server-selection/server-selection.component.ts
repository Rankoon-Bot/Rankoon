import { Component, HostListener, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { AuthService, Guild } from '../../services/auth.service';
import { AuthStore } from '../../store/auth.store';
import { AppStore } from '../../store/app.store';

@Component({
  selector: 'app-server-selection',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="server-selection">
      <div class="server-selection-header">
        <div class="header-content">
          <h1>Server auswählen</h1>
          <p>Wähle einen Discord Server aus, um das Dashboard zu nutzen.</p>
        </div>
      </div>

      <div class="server-grid" *ngIf="!appStore.isLoading() && appStore.hasGuilds(); else loadingOrEmpty">
        <div 
          class="server-card" 
          *ngFor="let guild of appStore.guilds()" 
          (click)="selectServer(guild)"
          [class.owner]="guild.owner"
          [class.missing]="guild.botInstalled !== true"
          [attr.role]="guild.botInstalled === true ? 'button' : null"
          [attr.tabindex]="guild.botInstalled === true ? 0 : null"
          [attr.aria-label]="guild.botInstalled === true ? guild.name + ' auswählen' : null"
          (keydown.enter)="selectServer(guild)"
          (keydown.space)="selectServer(guild); $event.preventDefault()"
        >
          <div class="server-icon">
            <img 
              *ngIf="guild.icon" 
              [src]="getGuildIconUrl(guild)" 
              [alt]="guild.name"
              class="guild-icon"
            >
            <div *ngIf="!guild.icon" class="guild-initials">
              {{ getGuildInitials(guild.name) }}
            </div>
          </div>
          <div class="server-info">
            <h3 class="server-name">{{ guild.name }}</h3>
            <div class="server-badges">
              <span *ngIf="guild.owner" class="badge owner-badge">
                <svg xmlns="http://www.w3.org/2000/svg" width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <path d="M12 2L2 7l10 5 10-5-10-5z"/>
                  <path d="m2 17 10 5 10-5"/>
                  <path d="m2 12 10 5 10-5"/>
                </svg>
                Owner
              </span>
              <span class="badge admin-badge" *ngIf="!guild.owner && hasAdminPermissions(guild)">
                <svg xmlns="http://www.w3.org/2000/svg" width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <rect x="2" y="3" width="20" height="4" rx="1"/>
                  <rect x="2" y="9" width="20" height="4" rx="1"/>
                  <rect x="2" y="15" width="20" height="4" rx="1"/>
                </svg>
                Manager
              </span>
              <span class="badge bot-badge" *ngIf="guild.botInstalled">
                Bot aktiv
              </span>
              <span class="badge missing-badge" *ngIf="guild.botInstalled !== true">
                Bot fehlt
              </span>
            </div>
          </div>
          <a
            *ngIf="guild.botInstalled !== true && guild.inviteUrl"
            class="invite-btn"
            [href]="guild.inviteUrl"
            target="_blank"
            rel="noopener noreferrer"
            (click)="inviteBot($event)"
          >
            Bot einladen
          </a>
          <div class="server-arrow" *ngIf="guild.botInstalled">
            <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <polyline points="9,18 15,12 9,6"/>
            </svg>
          </div>
        </div>
      </div>

      <ng-template #loadingOrEmpty>
        <div class="center-content">
          <div *ngIf="appStore.isLoading()" class="loading-state">
            <div class="loading-spinner"></div>
            <h3>Server werden geladen...</h3>
            <p>Einen Moment bitte, während wir deine Discord Server abrufen.</p>
          </div>
          
          <div *ngIf="!appStore.isLoading() && !appStore.hasGuilds()" class="empty-state">
            <div class="empty-icon">
              <svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <rect x="2" y="3" width="20" height="4" rx="1"/>
                <rect x="2" y="9" width="20" height="4" rx="1"/>
                <rect x="2" y="15" width="20" height="4" rx="1"/>
              </svg>
            </div>
            <h3>Keine Server gefunden</h3>
            <p>Du scheinst noch keine Discord Server zu verwalten oder zu besitzen.</p>
            <button class="retry-btn" (click)="loadGuilds()">
              <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <polyline points="23,4 23,10 17,10"/>
                <path d="M20.49,15A9,9,0,1,1,5.64,5.64L23,10"/>
              </svg>
              Erneut versuchen
            </button>
          </div>
        </div>
      </ng-template>

      <div *ngIf="appStore.hasError()" class="error-state">
        <div class="error-icon">
          <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <circle cx="12" cy="12" r="10"/>
            <line x1="15" y1="9" x2="9" y2="15"/>
            <line x1="9" y1="9" x2="15" y2="15"/>
          </svg>
        </div>
        <p class="error-message">{{ appStore.error() }}</p>
        <button class="retry-btn" (click)="loadGuilds()">Erneut versuchen</button>
      </div>
    </div>
  `,
  styleUrls: ['./server-selection.component.scss']
})
export class ServerSelectionComponent implements OnInit {
  private readonly authService = inject(AuthService);
  private readonly authStore = inject(AuthStore);
  private readonly router = inject(Router);
  readonly appStore = inject(AppStore);
  private refreshAfterInvite = false;

  ngOnInit(): void {
    this.loadGuilds();
  }

  loadGuilds(): void {
    this.appStore.setLoading(true);
    this.appStore.setError(null);

    this.authService.getUserGuilds().subscribe({
      next: (guilds) => {
        this.appStore.setGuilds(guilds);
        this.appStore.setLoading(false);
      },
      error: (error) => {
        console.error('Error loading guilds:', error);
        this.appStore.setError('Fehler beim Laden der Server. Bitte versuchen Sie es erneut.');
        this.appStore.setLoading(false);
      }
    });
  }

  selectServer(guild: Guild): void {
    if (!guild.botInstalled) return;
    this.appStore.setSelectedGuild(guild);
    this.router.navigate(['/dashboard']);
  }

  inviteBot(event: Event): void {
    event.stopPropagation();
    this.refreshAfterInvite = true;
  }

  @HostListener('window:focus')
  refreshGuildsAfterInvite(): void {
    if (!this.refreshAfterInvite) return;
    this.refreshAfterInvite = false;
    this.loadGuilds();
  }

  getGuildIconUrl(guild: Guild): string {
    if (!guild.icon) return '';
    return `https://cdn.discordapp.com/icons/${guild.id}/${guild.icon}.png?size=64`;
  }

  getGuildInitials(name: string): string {
    return name
      .split(' ')
      .map(word => word.charAt(0))
      .join('')
      .substring(0, 2)
      .toUpperCase();
  }

  hasAdminPermissions(guild: Guild): boolean {
    // Check if user has administrator or manage guild permissions
    // Discord permission values: Administrator = 8, Manage Guild = 32
    const permissions = parseInt(guild.permissions);
    const hasAdmin = (permissions & 8) === 8; // Administrator
    const hasManageGuild = (permissions & 32) === 32; // Manage Guild
    return hasAdmin || hasManageGuild;
  }
}
