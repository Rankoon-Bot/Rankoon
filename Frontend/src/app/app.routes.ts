import { Routes } from '@angular/router';
import {
  guildGuard,
  guestGuard,
  moduleGuard,
  ownerGuard,
  serverSelectionGuard,
  settingsGuard,
} from './guards/auth.guard';
import { MainLayoutComponent } from './layout/main-layout/main-layout.component';
import { translationScope } from './i18n/module-translation.resolver';

export const routes: Routes = [
  {
    path: '',
    component: MainLayoutComponent,
    children: [
      {
        path: '',
        loadComponent: () =>
          import('./pages/landing/landing.component').then(
            (c) => c.LandingComponent,
          ),
        canActivate: [guestGuard],
        resolve: { translations: translationScope('landing') },
      },
      {
        path: 'login',
        loadComponent: () =>
          import('./pages/login/login.component').then((c) => c.LoginComponent),
        canActivate: [guestGuard],
        resolve: { translations: translationScope('auth') },
      },
      {
        path: 'tos',
        loadComponent: () =>
          import('./pages/legal/legal.component').then((c) => c.LegalComponent),
        data: { page: 'tos' },
      },
      {
        path: 'privacy',
        loadComponent: () =>
          import('./pages/legal/legal.component').then((c) => c.LegalComponent),
        data: { page: 'privacy' },
      },
      {
        path: 'auth/callback',
        loadComponent: () =>
          import('./pages/auth-callback/auth-callback.component').then(
            (c) => c.AuthCallbackComponent,
          ),
        resolve: { translations: translationScope('auth') },
      },
      {
        path: 'rankings/:alias',
        loadComponent: () =>
          import('./pages/leaderboard/leaderboard.component').then(
            (c) => c.LeaderboardComponent,
          ),
        resolve: { translations: translationScope('leaderboard') },
      },
      {
        path: 'server-selection',
        loadComponent: () =>
          import('./pages/server-selection/server-selection.component').then(
            (c) => c.ServerSelectionComponent,
          ),
        canActivate: [serverSelectionGuard],
        resolve: { translations: translationScope('server-selection') },
      },
      {
        path: 'dashboard',
        loadComponent: () =>
          import('./pages/dashboard/dashboard.component').then(
            (c) => c.DashboardComponent,
          ),
        canActivate: [guildGuard, settingsGuard],
        resolve: { translations: translationScope('dashboard') },
      },
      {
        path: 'xp',
        loadComponent: () =>
          import('./pages/xp-config/xp-config.component').then(
            (c) => c.XpConfigComponent,
          ),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'xp' },
        resolve: { translations: translationScope('xp') },
      },
      {
        path: 'vc-hubs',
        loadComponent: () =>
          import('./pages/vc-hubs/vc-hubs.component').then(
            (c) => c.VcHubsComponent,
          ),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'voice-hubs' },
        resolve: { translations: translationScope('voice-hubs') },
      },
      {
        path: 'server-config',
        redirectTo: 'server-config/leaderboard',
        pathMatch: 'full',
      },
      {
        path: 'server-config/leaderboard',
        loadComponent: () =>
          import('./pages/leaderboard-settings/leaderboard-settings.component').then(
            (c) => c.LeaderboardSettingsComponent,
          ),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'leaderboard' },
        resolve: { translations: translationScope('leaderboard-settings') },
      },
      {
        path: 'server-config/roles',
        loadComponent: () =>
          import('./pages/role-permissions/role-permissions.component').then(
            (c) => c.RolePermissionsComponent,
          ),
        canActivate: [guildGuard, ownerGuard],
        resolve: { translations: translationScope('role-permissions') },
      },
      { path: 'logs', redirectTo: '/logs/activity', pathMatch: 'full' },
      {
        path: 'logs/activity',
        loadComponent: () =>
          import('./pages/reports/activity-logs.component').then(
            (c) => c.ActivityLogsComponent,
          ),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'reporting' },
        resolve: { translations: translationScope('reporting') },
      },
      {
        path: 'logs/commands',
        loadComponent: () =>
          import('./pages/reports/command-usage.component').then(
            (c) => c.CommandUsageComponent,
          ),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'reporting' },
        resolve: { translations: translationScope('reporting') },
      },
      {
        path: 'logs/errors',
        loadComponent: () =>
          import('./pages/reports/error-logs.component').then(
            (c) => c.ErrorLogsComponent,
          ),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'reporting' },
        resolve: { translations: translationScope('reporting') },
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
