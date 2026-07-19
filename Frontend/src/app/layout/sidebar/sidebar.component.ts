import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { LayoutStateService } from '../layout-state.service';

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
    <aside class="sidebar" [class.mobile-open]="layoutState.mobileNavigationOpen()">
      <nav class="sidebar-nav">
        <ul class="nav-list">
          <li *ngFor="let item of menuItems" class="nav-item">
            <a 
                [routerLink]="item.route" (click)="layoutState.closeMobileNavigation()"
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
    </aside>
  `,
  styleUrls: ['./sidebar.component.scss']
})
export class SidebarComponent {
  constructor(public readonly layoutState: LayoutStateService) {}

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
      label: 'XP & Level',
      route: '/xp',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="m12 2 3.1 6.3L22 9.3l-5 4.9 1.2 6.8-6.2-3.3-6.2 3.3L7 14.2 2 9.3l6.9-1z"/></svg>`
    },
    {
      label: 'VC-Hubs',
      route: '/vc-hubs',
      icon: `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 1a9 9 0 0 0-9 9v4a3 3 0 0 0 3 3h2v-7H5.1A7 7 0 0 1 19 10h-3v7h2a3 3 0 0 0 3-3v-4a9 9 0 0 0-9-9Z"/><path d="M12 19v4"/></svg>`
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
        { label: 'Rangliste', route: '/server-config/leaderboard', icon: '' },
        { label: 'Allgemeine Einstellungen', route: '/server-config/general', icon: '' },
        { label: 'Rollen & Berechtigungen', route: '/server-config/roles', icon: '' },
        { label: 'Kanäle', route: '/server-config/channels', icon: '' }
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
