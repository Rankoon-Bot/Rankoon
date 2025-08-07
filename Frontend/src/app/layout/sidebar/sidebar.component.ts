import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';

interface MenuItem {
  label: string;
  route: string;
  icon: string;
  children?: MenuItem[];
}

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [CommonModule, RouterModule],
  template: `
    <aside class="sidebar">
      <nav class="sidebar-nav">
        <ul class="nav-list">
          <li *ngFor="let item of menuItems" class="nav-item">
            <a 
              [routerLink]="item.route" 
              routerLinkActive="active"
              class="nav-link"
            >
              <i [innerHTML]="item.icon" class="nav-icon"></i>
              <span class="nav-label">{{ item.label }}</span>
            </a>
            <ul *ngIf="item.children" class="sub-nav">
              <li *ngFor="let child of item.children" class="sub-nav-item">
                <a 
                  [routerLink]="child.route" 
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
    </aside>
  `,
  styleUrls: ['./sidebar.component.scss']
})
export class SidebarComponent {
  menuItems: MenuItem[] = [
    {
      label: 'Dashboard',
      route: '/dashboard',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <rect x="3" y="3" width="7" height="9"/>
        <rect x="14" y="3" width="7" height="5"/>
        <rect x="14" y="12" width="7" height="9"/>
        <rect x="3" y="16" width="7" height="5"/>
      </svg>`
    },
    {
      label: 'Server Konfiguration',
      route: '/server-config',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <rect x="2" y="3" width="20" height="4" rx="1"/>
        <rect x="2" y="9" width="20" height="4" rx="1"/>
        <rect x="2" y="15" width="20" height="4" rx="1"/>
        <line x1="6" y1="5" x2="6.01" y2="5"/>
        <line x1="6" y1="11" x2="6.01" y2="11"/>
        <line x1="6" y1="17" x2="6.01" y2="17"/>
      </svg>`,
      children: [
        { label: 'Allgemeine Einstellungen', route: '/server-config/general', icon: '' },
        { label: 'Rollen & Berechtigungen', route: '/server-config/roles', icon: '' },
        { label: 'Kanäle', route: '/server-config/channels', icon: '' }
      ]
    },
    {
      label: 'Moderation',
      route: '/moderation',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <path d="M12 2L2 7l10 5 10-5-10-5z"/>
        <path d="m2 17 10 5 10-5"/>
        <path d="m2 12 10 5 10-5"/>
      </svg>`,
      children: [
        { label: 'Automod', route: '/moderation/automod', icon: '' },
        { label: 'Warn System', route: '/moderation/warnings', icon: '' },
        { label: 'Banns & Timeouts', route: '/moderation/bans', icon: '' }
      ]
    },
    {
      label: 'Economy',
      route: '/economy',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <line x1="12" y1="1" x2="12" y2="23"/>
        <path d="M17 5H9.5a3.5 3.5 0 0 0 0 7h5a3.5 3.5 0 0 1 0 7H6"/>
      </svg>`,
      children: [
        { label: 'Currency Einstellungen', route: '/economy/currency', icon: '' },
        { label: 'Shop System', route: '/economy/shop', icon: '' },
        { label: 'Rewards', route: '/economy/rewards', icon: '' }
      ]
    },
    {
      label: 'Fun & Games',
      route: '/fun',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <path d="M8 21h8"/>
        <path d="M12 17v4"/>
        <path d="m5 7 1-1h12l1 1"/>
        <path d="M12 11v5"/>
        <path d="M8 7v4"/>
        <path d="M16 7v4"/>
      </svg>`,
      children: [
        { label: 'Mini Games', route: '/fun/games', icon: '' },
        { label: 'Custom Commands', route: '/fun/commands', icon: '' },
        { label: 'Reaktionen', route: '/fun/reactions', icon: '' }
      ]
    },
    {
      label: 'Logs & Analytics',
      route: '/logs',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
        <polyline points="14,2 14,8 20,8"/>
        <line x1="16" y1="13" x2="8" y2="13"/>
        <line x1="16" y1="17" x2="8" y2="17"/>
        <polyline points="10,9 9,9 8,9"/>
      </svg>`,
      children: [
        { label: 'Activity Logs', route: '/logs/activity', icon: '' },
        { label: 'Command Usage', route: '/logs/commands', icon: '' },
        { label: 'Error Logs', route: '/logs/errors', icon: '' }
      ]
    }
  ];
}
