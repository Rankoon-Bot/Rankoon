import { Component, ElementRef, HostListener, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { AuthStore } from '../../store/auth.store';
import { AppStore, Guild } from '../../store/app.store';
import { AuthService } from '../../services/auth.service';
import { LayoutStateService } from '../layout-state.service';
import { GuildAccessService } from '../../services/guild-access.service';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { LanguageSwitcherComponent } from '../../shared/language-switcher/language-switcher.component';
import { ApiErrorService } from '../../services/api-error.service';

@Component({
    selector: 'app-header',
    standalone: true,
    imports: [CommonModule, RouterLink, TranslocoPipe, LanguageSwitcherComponent],
    template: `
        <header class="header">
            <div class="header-left">
                <button *ngIf="authStore.isAuthenticated()" class="menu-btn" type="button" [attr.aria-label]="'header.toggleNavigation' | transloco" [attr.aria-expanded]="layoutState.mobileNavigationOpen()" aria-controls="primary-navigation" (click)="layoutState.toggleMobileNavigation()">
                    <svg aria-hidden="true" xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><line x1="4" y1="6" x2="20" y2="6"/><line x1="4" y1="12" x2="20" y2="12"/><line x1="4" y1="18" x2="20" y2="18"/></svg>
                </button>
                <a class="logo" routerLink="/">Rankoon</a>
                <div *ngIf="appStore.hasSelectedGuild()" class="guild-picker">
                  <button #guildTrigger class="guild-info" type="button" aria-haspopup="menu" [attr.aria-expanded]="isGuildDropdownOpen" (click)="toggleGuildDropdown()">
                    <div class="guild-icon">
                        <img 
                            *ngIf="appStore.selectedGuild()?.icon" 
                            [src]="getGuildIconUrl()"
                            [alt]="appStore.selectedGuild()?.name"
                            class="guild-avatar"
                        >
                        <div *ngIf="!appStore.selectedGuild()?.icon" class="guild-initials">
                            {{ getGuildInitials() }}
                        </div>
                    </div>
                    <div class="guild-name">
                        <span>{{ appStore.selectedGuild()?.name }}</span>
                        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <polyline [attr.points]="isGuildDropdownOpen ? '18,15 12,9 6,15' : '6,9 12,15 18,9'"/>
                        </svg>
                    </div>

                  </button>
                    <!-- Guild Dropdown -->
                    <div *ngIf="isGuildDropdownOpen" class="guild-dropdown">
                        <div class="dropdown-header">
                            <span>{{ 'header.switchServer' | transloco }}</span>
                        </div>
                        <div class="dropdown-content">
                            <div *ngIf="appStore.isLoading()" class="dropdown-loading">
                                <div class="loading-spinner"></div>
                                <span>{{ 'common.loadingServers' | transloco }}</span>
                            </div>
                            
                            <div *ngIf="!appStore.isLoading() && appStore.hasGuilds()" class="guild-list">
                                <button
                                    *ngFor="let guild of appStore.guilds()"
                                  class="guild-item"
                                     type="button"
                                     [disabled]="guild.botInstalled !== true"
                                     [class.selected]="guild.id === appStore.selectedGuild()?.id"
                                     [class.missing]="guild.botInstalled !== true"
                                     (click)="selectGuild(guild)"
                                >
                                    <div class="guild-item-icon">
                                        <img 
                                            *ngIf="guild.icon" 
                                            [src]="getGuildIconUrl(guild)"
                                            [alt]="guild.name"
                                            class="guild-item-avatar"
                                        >
                                        <div *ngIf="!guild.icon" class="guild-item-initials">
                                            {{ getGuildInitials(guild) }}
                                        </div>
                                    </div>
                                    <div class="guild-item-info">
                                        <span class="guild-item-name">{{ guild.name }}</span>
                                        <div class="guild-item-badges">
                                             <span *ngIf="guild.owner" class="badge owner-badge">{{ 'common.owner' | transloco }}</span>
                                             <span *ngIf="!guild.owner && hasAdminPermissions(guild)" class="badge manager-badge">{{ 'common.manager' | transloco }}</span>
                                             <span *ngIf="guild.botInstalled !== true" class="badge missing-badge">{{ 'common.botMissing' | transloco }}</span>
                                        </div>
                                    </div>
                                    <div *ngIf="guild.id === appStore.selectedGuild()?.id" class="selected-indicator">
                                        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                                            <polyline points="20,6 9,17 4,12"/>
                                        </svg>
                                    </div>
                                </button>
                            </div>

                            <div *ngIf="!appStore.isLoading() && !appStore.hasGuilds()" class="dropdown-empty">
                                 <span>{{ 'common.noServers' | transloco }}</span>
                            </div>

                            <div *ngIf="appStore.hasError()" class="dropdown-error">
                                <span>{{ appStore.error() }}</span>
                                  <button class="retry-btn" type="button" (click)="loadGuilds()">{{ 'common.retry' | transloco }}</button>
                            </div>
                        </div>
                        
                        <div class="dropdown-footer">
                            <a
                                *ngIf="botInviteUrl"
                                class="invite-btn"
                                [href]="botInviteUrl"
                                target="_blank"
                                rel="noopener noreferrer"
                                (click)="inviteBot()"
                            >
                                {{ 'header.inviteBot' | transloco }}
                            </a>
                            <button class="view-all-btn" type="button" (click)="goToServerSelection()">
                                 {{ 'header.allServers' | transloco }}
                            </button>
                        </div>
                    </div>
                </div>
            </div>
            <div class="header-right">
                <app-language-switcher />
                <div *ngIf="authStore.isAuthenticated()" class="user-info">
                    <img 
                        [src]="getAvatarUrl()"
                        [alt]="authStore.user()?.username"
                        class="user-avatar"
                    >
                    <span class="username">{{ authStore.user()?.username }}</span>
                    <button class="logout-btn" (click)="logout()" [attr.aria-label]="'header.logout' | transloco">
                        <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                            <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/>
                            <polyline points="16,17 21,12 16,7"/>
                            <line x1="21" x2="9" y1="12" y2="12"/>
                        </svg>
                    </button>
                </div>
            </div>
        </header>
    `,
    styleUrls: ['./header.component.scss']
})
export class HeaderComponent implements OnInit {
    @ViewChild('guildTrigger') private guildTrigger?: ElementRef<HTMLButtonElement>;
    isGuildDropdownOpen = false;
    botInviteUrl = '';
    private refreshAfterInvite = false;

    constructor(
        public authStore: AuthStore,
        public appStore: AppStore,
        private authService: AuthService,
        private router: Router,
        public readonly layoutState: LayoutStateService,
        private readonly guildAccess: GuildAccessService,
        private readonly i18n: TranslocoService,
        private readonly apiErrors: ApiErrorService
    ) {}

    ngOnInit(): void {
        // Load guilds if not already loaded and user is authenticated
        if (this.authStore.isAuthenticated() && !this.appStore.hasGuilds()) {
            this.loadGuilds();
        }
        this.authService.getBotInviteUrl().subscribe({ next: url => this.botInviteUrl = url });
    }

    @HostListener('document:click', ['$event'])
    onDocumentClick(event: Event): void {
        const target = event.target as HTMLElement;
        const guildDropdown = target.closest('.guild-info, .guild-dropdown');
        
        if (!guildDropdown) {
            this.closeGuildDropdown();
        }
    }

    @HostListener('document:keydown.escape')
    onEscape(): void {
        if (this.isGuildDropdownOpen) this.closeGuildDropdown(true);
        if (this.layoutState.mobileNavigationOpen()) this.layoutState.closeMobileNavigation();
    }

    @HostListener('window:focus')
    refreshGuildsAfterInvite(): void {
        if (!this.refreshAfterInvite) return;
        this.refreshAfterInvite = false;
        this.loadGuilds(true);
    }

    getAvatarUrl(): string | undefined {
        const user = this.authStore.user();
        if (user && user.discordId && user.avatar) {
            return `https://cdn.discordapp.com/avatars/${user.discordId}/${user.avatar}.webp?size=128`;
        }
        return undefined;
    }

    goToServerSelection(): void {
        this.closeGuildDropdown();
        this.appStore.setError(null);
        void this.router.navigate(['/server-selection']);
    }

    toggleGuildDropdown(): void {
        this.isGuildDropdownOpen = !this.isGuildDropdownOpen;
        
        // Load guilds if not loaded yet
        if (this.isGuildDropdownOpen) {
            this.loadGuilds();
        }
    }

    selectGuild(guild: Guild): void {
        if (!guild.botInstalled) return;
        this.closeGuildDropdown();
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
                void this.router.navigate(['/server-selection'], {
                    queryParams: { access: error?.status === 403 ? 'forbidden' : 'unavailable' }
                });
            }
        });
    }

    inviteBot(): void {
        this.refreshAfterInvite = true;
    }

    loadGuilds(refresh = false): void {
        if (!this.authStore.isAuthenticated()) {
            return;
        }

        this.appStore.setLoading(true);
        this.authService.getUserGuilds(refresh).subscribe({
            next: (guilds) => {
                this.appStore.setGuilds(guilds);
                this.appStore.setLoading(false);
            },
            error: (error) => {
                 this.appStore.setError(this.apiErrors.resolve(error, 'errors.guildsLoad').message);
                this.appStore.setLoading(false);
            }
        });
    }

    getGuildIconUrl(guild?: Guild): string {
        const targetGuild = guild || this.appStore.selectedGuild();
        if (targetGuild && targetGuild.icon) {
            return `https://cdn.discordapp.com/icons/${targetGuild.id}/${targetGuild.icon}.png?size=64`;
        }
        return '';
    }

    getGuildInitials(guild?: Guild): string {
        const targetGuild = guild || this.appStore.selectedGuild();
        if (targetGuild) {
            return targetGuild.name
                .split(' ')
                .map(word => word.charAt(0))
                .join('')
                .substring(0, 2)
                .toUpperCase();
        }
        return '';
    }

    private closeGuildDropdown(restoreFocus = false): void {
        this.isGuildDropdownOpen = false;
        if (restoreFocus) queueMicrotask(() => this.guildTrigger?.nativeElement.focus());
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

    logout(): void {
        this.authService.logout();
    }
}
