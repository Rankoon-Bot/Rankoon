import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AuthStore } from '../../store/auth.store';
import { AuthService } from '../../services/auth.service';

@Component({
    selector: 'app-header',
    standalone: true,
    imports: [CommonModule],
    template: `
        <header class="header">
            <div class="header-left">
                <h1 class="logo">Rankoon Dashboard</h1>
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
export class HeaderComponent {
    constructor(
        public authStore: AuthStore,
        private authService: AuthService
    ) {}

    getAvatarUrl(): string | undefined {
        const user = this.authStore.user();
        if (user && user.discordId && user.avatar) {
            return `https://cdn.discordapp.com/avatars/${user.discordId}/${user.avatar}.webp?size=128`;
        }
        return undefined;
    }

    logout(): void {
        this.authService.logout();
    }
}
