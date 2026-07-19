import { Component, HostListener, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AuthService } from '../../services/auth.service';
import { AuthStore } from '../../store/auth.store';
import { AppStore, Guild } from '../../store/app.store';
import { GuildAccessService } from '../../services/guild-access.service';
import { ActivatedRoute } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiErrorService } from '../../services/api-error.service';

@Component({
  selector: 'app-server-selection',
  standalone: true,
  imports: [CommonModule, TranslocoPipe],
  template: `
    <div class="server-selection">
      <div class="server-selection-header">
        <div class="header-content">
          <h1>{{ 'serverSelection.title' | transloco }}</h1>
          <p>{{ 'serverSelection.subtitle' | transloco }}</p>
        </div>
      </div>

      <p *ngIf="accessNotice()" class="access-notice" role="alert">{{ accessNotice() }}</p>

      <div class="server-grid" *ngIf="!appStore.isLoading() && appStore.hasGuilds(); else loadingOrEmpty">
        <div 
          class="server-card" 
          *ngFor="let guild of appStore.guilds()" 
          (click)="selectServer(guild)"
          [class.owner]="guild.owner"
          [class.missing]="guild.botInstalled !== true"
          [attr.role]="guild.botInstalled === true ? 'button' : null"
          [attr.tabindex]="guild.botInstalled === true ? 0 : null"
          [attr.aria-label]="(guild.botInstalled === true ? 'serverSelection.selectAria' : 'serverSelection.missingAria') | transloco: { name: guild.name }"
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
                {{ 'common.owner' | transloco }}
              </span>
              <span class="badge admin-badge" *ngIf="!guild.owner && hasAdminPermissions(guild)">
                <svg xmlns="http://www.w3.org/2000/svg" width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <rect x="2" y="3" width="20" height="4" rx="1"/>
                  <rect x="2" y="9" width="20" height="4" rx="1"/>
                  <rect x="2" y="15" width="20" height="4" rx="1"/>
                </svg>
                {{ 'common.manager' | transloco }}
              </span>
              <span class="badge bot-badge" *ngIf="guild.botInstalled">
                {{ 'serverSelection.botActive' | transloco }}
              </span>
              <span class="badge missing-badge" *ngIf="guild.botInstalled !== true">
                {{ (canInviteBot(guild) ? 'common.botMissing' : 'common.unavailable') | transloco }}
              </span>
            </div>
          </div>
          <a
            *ngIf="guild.botInstalled !== true && guild.inviteUrl && canInviteBot(guild)"
            class="invite-btn"
            [href]="guild.inviteUrl"
            target="_blank"
            rel="noopener noreferrer"
            (click)="inviteBot($event)"
          >
            {{ 'serverSelection.inviteBot' | transloco }}
          </a>
          <span class="unavailable-copy" *ngIf="guild.botInstalled !== true && !canInviteBot(guild)">{{ 'serverSelection.managerInvite' | transloco }}</span>
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
            <h3>{{ 'common.loadingServers' | transloco }}</h3>
            <p>{{ 'serverSelection.loadingHint' | transloco }}</p>
          </div>
          
          <div *ngIf="!appStore.isLoading() && !appStore.hasGuilds()" class="empty-state">
            <div class="empty-icon">
              <svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <rect x="2" y="3" width="20" height="4" rx="1"/>
                <rect x="2" y="9" width="20" height="4" rx="1"/>
                <rect x="2" y="15" width="20" height="4" rx="1"/>
              </svg>
            </div>
            <h3>{{ 'common.noServers' | transloco }}</h3>
            <p>{{ 'serverSelection.empty' | transloco }}</p>
            <button class="retry-btn" (click)="loadGuilds()">
              <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <polyline points="23,4 23,10 17,10"/>
                <path d="M20.49,15A9,9,0,1,1,5.64,5.64L23,10"/>
              </svg>
              {{ 'common.retry' | transloco }}
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
        <button class="retry-btn" (click)="loadGuilds()">{{ 'common.retry' | transloco }}</button>
      </div>
    </div>
  `,
  styleUrls: ['./server-selection.component.scss']
})
export class ServerSelectionComponent implements OnInit {
  private readonly authService = inject(AuthService);
  private readonly authStore = inject(AuthStore);
  private readonly guildAccess = inject(GuildAccessService);
  private readonly route = inject(ActivatedRoute);
  private readonly i18n = inject(TranslocoService);
  private readonly apiErrors = inject(ApiErrorService);
  readonly appStore = inject(AppStore);
  readonly accessNotice = signal('');
  private refreshAfterInvite = false;

  ngOnInit(): void {
    const access = this.route.snapshot.queryParamMap.get('access');
    if (access === 'forbidden') this.accessNotice.set(this.i18n.translate('errors.settingsForbidden'));
    if (access === 'unavailable') this.accessNotice.set(this.i18n.translate('errors.capabilitiesCheck'));
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
        this.appStore.setError(this.apiErrors.resolve(error, 'errors.guildsLoad').message);
        this.appStore.setLoading(false);
      }
    });
  }

  selectServer(guild: Guild): void {
    if (!guild.botInstalled) return;
    this.appStore.setLoading(true);
    this.appStore.setError(null);
    this.guildAccess.selectAndNavigate(guild).subscribe({
      next: () => this.appStore.setLoading(false),
      error: (error) => {
        if (this.appStore.selectedGuild()?.id !== guild.id) return;
        this.appStore.setLoading(false);
        this.appStore.setSelectedGuild(null);
        this.appStore.setError(error?.status === 403
          ? this.i18n.translate('errors.guildForbidden')
          : this.apiErrors.resolve(error, 'errors.capabilitiesLoad').message);
      }
    });
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
    try {
      const permissions = BigInt(guild.permissions);
      return (permissions & 8n) === 8n || (permissions & 32n) === 32n;
    } catch {
      return false;
    }
  }

  canInviteBot(guild: Guild): boolean {
    return guild.owner || this.hasAdminPermissions(guild);
  }
}
