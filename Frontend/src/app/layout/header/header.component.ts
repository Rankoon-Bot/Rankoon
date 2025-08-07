import { Component, OnInit, HostListener } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { AuthStore } from '../../store/auth.store';
import { AppStore } from '../../store/app.store';
import { AuthService } from '../../services/auth.service';
import { Guild } from '../../services/auth.service';
import { LayoutStateService } from '../layout-state.service';

@Component({
    selector: 'app-header',
    standalone: true,
    imports: [CommonModule],
    template: `
        <header class="header">
            <div class="header-left">
                <button class="menu-btn" type="button" aria-label="Navigation umschalten" (click)="layoutState.toggleMobileNavigation()">
                    <svg aria-hidden="true" xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><line x1="4" y1="6" x2="20" y2="6"/><line x1="4" y1="12" x2="20" y2="12"/><line x1="4" y1="18" x2="20" y2="18"/></svg>
                </button>
                <h1 class="logo">Rankoon Dashboard</h1>
                <div *ngIf="appStore.hasSelectedGuild()" class="guild-info" (click)="toggleGuildDropdown()">
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

                    <!-- Guild Dropdown -->
                    <div *ngIf="isGuildDropdownOpen" class="guild-dropdown">
                        <div class="dropdown-header">
                            <span>Server wechseln</span>
                        </div>
                        <div class="dropdown-content">
                            <div *ngIf="appStore.isLoading()" class="dropdown-loading">
                                <div class="loading-spinner"></div>
                                <span>Server werden geladen...</span>
                            </div>
                            
                            <div *ngIf="!appStore.isLoading() && appStore.hasGuilds()" class="guild-list">
                                <div 
                                    *ngFor="let guild of appStore.guilds()" 
                                    class="guild-item"
                                    [class.selected]="guild.id === appStore.selectedGuild()?.id"
                                    [class.missing]="guild.botInstalled !== true"
                                    (click)="selectGuild(guild); $event.stopPropagation()"
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
                                            <span *ngIf="guild.owner" class="badge owner-badge">Owner</span>
                                            <span *ngIf="!guild.owner" class="badge manager-badge">Manager</span>
                                            <span *ngIf="guild.botInstalled !== true" class="badge missing-badge">Bot fehlt</span>
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
                                        Einladen
                                    </a>
                                    <div *ngIf="guild.id === appStore.selectedGuild()?.id" class="selected-indicator">
                                        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                                            <polyline points="20,6 9,17 4,12"/>
                                        </svg>
                                    </div>
                                </div>
                            </div>

                            <div *ngIf="!appStore.isLoading() && !appStore.hasGuilds()" class="dropdown-empty">
                                <span>Keine Server gefunden</span>
                            </div>

                            <div *ngIf="appStore.hasError()" class="dropdown-error">
                                <span>{{ appStore.error() }}</span>
                                <button class="retry-btn" (click)="loadGuilds(); $event.stopPropagation()">Erneut versuchen</button>
                            </div>
                        </div>
                        
                        <div class="dropdown-footer">
                            <button class="view-all-btn" (click)="goToServerSelection(); $event.stopPropagation()">
                                Alle Server anzeigen
                            </button>
                        </div>
                    </div>
                </div>
            </div>
            <div class="header-right">
                <div *ngIf="authStore.isAuthenticated()" class="user-info">
                    <img 
                        [src]="getAvatarUrl()"
                        [alt]="authStore.user()?.username"
                        class="user-avatar"
                    >
                    <span class="username">{{ authStore.user()?.username }}</span>
                    <button class="logout-btn" (click)="logout()">
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
    isGuildDropdownOpen = false;
    private refreshAfterInvite = false;

    constructor(
        public authStore: AuthStore,
        public appStore: AppStore,
        private authService: AuthService,
        private router: Router,
        public readonly layoutState: LayoutStateService
    ) {}

    ngOnInit(): void {
        // Load guilds if not already loaded and user is authenticated
        if (this.authStore.isAuthenticated() && !this.appStore.hasGuilds()) {
            this.loadGuilds();
        }
    }

    @HostListener('document:click', ['$event'])
    onDocumentClick(event: Event): void {
        const target = event.target as HTMLElement;
        const guildDropdown = target.closest('.guild-info, .guild-dropdown');
        
        if (!guildDropdown) {
            this.isGuildDropdownOpen = false;
        }
    }

    @HostListener('window:focus')
    refreshGuildsAfterInvite(): void {
        if (!this.refreshAfterInvite) return;
        this.refreshAfterInvite = false;
        this.loadGuilds();
    }

    getAvatarUrl(): string | undefined {
        const user = this.authStore.user();
        if (user && user.discordId && user.avatar) {
            return `https://cdn.discordapp.com/avatars/${user.discordId}/${user.avatar}.webp?size=128`;
        }
        return undefined;
    }

    goToServerSelection(): void {
        console.log('Navigating to server selection...');
        this.isGuildDropdownOpen = false; // Close dropdown first
        
        // Clear any current error state
        this.appStore.setError(null);
        
        // Navigate to server selection
        this.router.navigate(['/server-selection']).then(success => {
            if (success) {
                console.log('Navigation to server-selection successful');
            } else {
                console.error('Navigation to server-selection failed');
            }
        }).catch(error => {
            console.error('Navigation error:', error);
        });
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
        this.appStore.setSelectedGuild(guild);
        this.isGuildDropdownOpen = false;
    }

    inviteBot(event: Event): void {
        event.stopPropagation();
        this.refreshAfterInvite = true;
    }

    loadGuilds(): void {
        if (!this.authStore.isAuthenticated()) {
            return;
        }

        this.appStore.setLoading(true);
        this.authService.getUserGuilds().subscribe({
            next: (guilds) => {
                this.appStore.setGuilds(guilds);
                this.appStore.setLoading(false);
            },
            error: (error) => {
                console.error('Error loading guilds in header:', error);
                this.appStore.setError('Fehler beim Laden der Server.');
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

    logout(): void {
        this.authService.logout();
    }
}
