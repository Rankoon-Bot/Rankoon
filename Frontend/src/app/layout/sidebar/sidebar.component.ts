import { Component, computed, effect, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { LayoutStateService } from '../layout-state.service';
import { AppStore } from '../../store/app.store';
import { GuildModuleId } from '../../models/guild-permissions.models';
import { TranslocoService } from '@jsverse/transloco';
import { LocaleService } from '../../i18n/locale.service';
import { TranslocoPipe } from '@jsverse/transloco';
import { environment } from '../../../environments/environment';
import { CustomBotIdentityAccessService } from '../../services/custom-bot-identity-access.service';
import { AuthStore } from '../../store/auth.store';
import { BuildInfoService } from '../../services/build-info.service';

interface MenuItem {
  label: string;
  route: string;
  icon: string;
  children?: MenuItem[];
}

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [CommonModule, RouterModule, TranslocoPipe],
  template: `
    <aside id="primary-navigation" class="sidebar" [class.mobile-open]="layoutState.mobileNavigationOpen()">
      <nav class="sidebar-nav" [attr.aria-label]="'nav.dashboard' | transloco">
        <ul class="nav-list">
          <li *ngFor="let item of menuItems()" class="nav-item" routerLinkActive="active">
            <a 
                [routerLink]="item.route" (click)="layoutState.closeMobileNavigation()"
              routerLinkActive="active"
              class="nav-link"
            >
              <i [innerHTML]="item.icon" class="nav-icon" aria-hidden="true"></i>
              <span class="nav-label">{{ item.label }}</span>
            </a>
            <ul *ngIf="item.children" class="sub-nav">
              <li *ngFor="let child of item.children" class="sub-nav-item">
                <a 
                  [routerLink]="child.route" 
                  (click)="layoutState.closeMobileNavigation()"
                  routerLinkActive="active"
                  class="sub-nav-link"
                >
                  {{ child.label }}
                </a>
              </li>
            </ul>
          </li>
        </ul>
      </nav>
      <footer class="sidebar-footer">v{{ buildInfo.buildVersion() }}</footer>
    </aside>
  `,
  styleUrls: ['./sidebar.component.scss']
})
export class SidebarComponent {
  readonly layoutState = inject(LayoutStateService);
  private readonly appStore = inject(AppStore);
  private readonly i18n = inject(TranslocoService);
  private readonly locale = inject(LocaleService);
  private readonly botIdentityAccess = inject(CustomBotIdentityAccessService);
  private readonly authStore = inject(AuthStore);
  readonly buildInfo = inject(BuildInfoService);

  constructor() { effect(() => { const guild = this.appStore.selectedGuild(); const capabilities = this.appStore.guildCapabilities(); if (guild && capabilities?.isOwner) this.botIdentityAccess.load(guild.id); else this.botIdentityAccess.clear(); }); }

  readonly menuItems = computed<MenuItem[]>(() => {
    this.locale.locale();
    const capabilities = this.appStore.guildCapabilities();
    const operatorItem = this.authStore.isBotOperator() ? [{ label: this.i18n.translate('nav.botManagement'), route: '/bot-management', icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="4" width="18" height="16" rx="2"/><path d="M8 9h8M8 13h5"/></svg>` }] : [];
    if (!capabilities || capabilities.guildId !== this.appStore.selectedGuild()?.id) return operatorItem;

    const items: MenuItem[] = [{
       label: this.i18n.translate('nav.leaderboard'),
      route: `/rankings/${capabilities.leaderboardAlias}`,
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 19V9M10 19V5M16 19v-7M22 19H2"/></svg>`
    }];

    if (capabilities.isOwner || capabilities.canAccessSettings) items.push({
       label: this.i18n.translate('nav.dashboard'),
      route: '/dashboard',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <rect x="3" y="3" width="7" height="9"/>
        <rect x="14" y="3" width="7" height="5"/>
        <rect x="14" y="12" width="7" height="9"/>
        <rect x="3" y="16" width="7" height="5"/>
      </svg>`
    });
    const hasModule = (moduleId: GuildModuleId) => capabilities.isOwner || capabilities.moduleIds.includes(moduleId);
    if (hasModule('xp')) items.push({
       label: this.i18n.translate('nav.xp'),
       route: '/xp',
       icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="m12 2 3.1 6.3L22 9.3l-5 4.9 1.2 6.8-6.2-3.3-6.2 3.3L7 14.2 2 9.3l6.9-1z"/></svg>`,
        children: [
            { label: this.i18n.translate('nav.seasons'), route: '/xp/seasons', icon: '' },
            ...(hasModule('xp-announcements') ? [{ label: this.i18n.translate('nav.levelUpAnnouncements'), route: '/xp/level-up-announcements', icon: '' }] : []),
           ...(hasModule('xp-audit') ? [{ label: this.i18n.translate('nav.xpAudit'), route: '/xp/audit', icon: '' }] : []),
          ...(hasModule('leaderboard') ? [{ label: this.i18n.translate('nav.leaderboardSettings'), route: '/server-config/leaderboard', icon: '' }] : [])
        ]
    });
    else if (hasModule('xp-audit')) items.push({
      label: this.i18n.translate('nav.xpAudit'),
      route: '/xp/audit',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 3v18h18"/><path d="m7 15 3-3 3 2 4-5"/></svg>`
    });
    if (hasModule('voice-hubs')) items.push({
       label: this.i18n.translate('nav.voiceHubs'),
      route: '/vc-hubs',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 1a9 9 0 0 0-9 9v4a3 3 0 0 0 3 3h2v-7H5.1A7 7 0 0 1 19 10h-3v7h2a3 3 0 0 0 3-3v-4a9 9 0 0 0-9-9Z"/><path d="M12 19v4"/></svg>`
    });
    if (hasModule('self-roles')) items.push({
       label: this.i18n.translate('nav.selfRoles'),
      route: '/self-roles',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 12a8 8 0 1 1-16 0 8 8 0 0 1 16 0Z"/><path d="m9 12 2 2 4-4"/></svg>`
    });

    if (capabilities.isOwner) items.push({
       label: this.i18n.translate('nav.roles'),
      route: '/server-config/roles',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="m17 11 2 2 4-4"/></svg>`
    });
    if (hasModule('reporting')) items.push({
       label: this.i18n.translate('nav.reports'),
      route: '/logs',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
        <polyline points="14,2 14,8 20,8"/>
        <line x1="16" y1="13" x2="8" y2="13"/>
        <line x1="16" y1="17" x2="8" y2="17"/>
        <polyline points="10,9 9,9 8,9"/>
      </svg>`,
      children: [
         { label: this.i18n.translate('nav.activity'), route: '/logs/activity', icon: '' },
         { label: this.i18n.translate('nav.commands'), route: '/logs/commands', icon: '' },
         { label: this.i18n.translate('nav.errors'), route: '/logs/errors', icon: '' }
      ]
    });
    if (capabilities.isOwner && this.botIdentityAccess.visible()) items.push({ label: this.i18n.translate('nav.botIdentity'), route: '/server-config/bot-identity', icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="4" y="4" width="16" height="16" rx="3"/><path d="M9 10h.01M15 10h.01M8 15h8"/></svg>` });
    if (hasModule('diagnostics')) items.push({
      label: this.i18n.translate('nav.diagnostics'), route: '/diagnostics/permissions',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 2a10 10 0 1 0 10 10"/><path d="m12 8-3 5 5-3"/></svg>`
    });
    else if (hasModule('xp-announcements')) items.push({ label: this.i18n.translate('nav.levelUpAnnouncements'), route: '/xp/level-up-announcements', icon: '' });
    if (!environment.production && capabilities.isOwner) items.push({
      label: this.i18n.translate('nav.dev'),
      route: '/dev',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="m8 9-4 3 4 3"/><path d="m16 9 4 3-4 3"/><path d="m14 5-4 14"/></svg>`,
      children: [{ label: this.i18n.translate('nav.devLeaderboard'), route: '/dev', icon: '' }]
    });
    return [...operatorItem, ...items];
  });
}
