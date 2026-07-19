import { Routes } from '@angular/router';
import { guildGuard, guestGuard, moduleGuard, ownerGuard, serverSelectionGuard, settingsGuard } from './guards/auth.guard';
import { MainLayoutComponent } from './layout/main-layout/main-layout.component';

export const routes: Routes = [
  {
    path: '',
    component: MainLayoutComponent,
    children: [
      { path: '', redirectTo: '/server-selection', pathMatch: 'full' },
      {
        path: 'login',
        loadComponent: () => import('./pages/login/login.component').then(c => c.LoginComponent),
        canActivate: [guestGuard]
      },
      {
        path: 'auth/callback',
        loadComponent: () => import('./pages/auth-callback/auth-callback.component').then(c => c.AuthCallbackComponent)
      },
      {
        path: 'rankings/:alias',
        loadComponent: () => import('./pages/leaderboard/leaderboard.component').then(c => c.LeaderboardComponent)
      },
      {
        path: 'server-selection',
        loadComponent: () => import('./pages/server-selection/server-selection.component').then(c => c.ServerSelectionComponent),
        canActivate: [serverSelectionGuard]
      },
      {
        path: 'dashboard',
        loadComponent: () => import('./pages/dashboard/dashboard.component').then(c => c.DashboardComponent),
        canActivate: [guildGuard, settingsGuard]
      },
      {
        path: 'xp',
        loadComponent: () => import('./pages/xp-config/xp-config.component').then(c => c.XpConfigComponent),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'xp' }
      },
      {
        path: 'vc-hubs',
        loadComponent: () => import('./pages/vc-hubs/vc-hubs.component').then(c => c.VcHubsComponent),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'voice-hubs' }
      },
      {
        path: 'server-config',
        redirectTo: 'server-config/leaderboard',
        pathMatch: 'full'
      },
      {
        path: 'server-config/leaderboard',
        loadComponent: () => import('./pages/leaderboard-settings/leaderboard-settings.component').then(c => c.LeaderboardSettingsComponent),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'leaderboard' }
      },
      {
        path: 'server-config/roles',
        loadComponent: () => import('./pages/role-permissions/role-permissions.component').then(c => c.RolePermissionsComponent),
        canActivate: [guildGuard, ownerGuard]
      },
      { path: 'logs', redirectTo: '/logs/activity', pathMatch: 'full' },
      {
        path: 'logs/activity',
        loadComponent: () => import('./pages/reports/activity-logs.component').then(c => c.ActivityLogsComponent),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'reporting' }
      },
      {
        path: 'logs/commands',
        loadComponent: () => import('./pages/reports/command-usage.component').then(c => c.CommandUsageComponent),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'reporting' }
      },
      {
        path: 'logs/errors',
        loadComponent: () => import('./pages/reports/error-logs.component').then(c => c.ErrorLogsComponent),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'reporting' }
      }
    ]
  },
  { path: '**', redirectTo: '/server-selection' }
];
