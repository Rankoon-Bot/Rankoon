import { Routes } from '@angular/router';
import {
  guildGuard,
  botOperatorGuard,
  guestGuard,
  moduleGuard,
  ownerGuard,
  serverSelectionGuard,
  settingsGuard,
} from './guards/auth.guard';
import { MainLayoutComponent } from './layout/main-layout/main-layout.component';
import { environment } from '../environments/environment';

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
      },
      {
        path: 'login',
        loadComponent: () =>
          import('./pages/login/login.component').then((c) => c.LoginComponent),
        canActivate: [guestGuard],
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
      },
      {
        path: 'rankings/:alias',
        loadComponent: () =>
          import('./pages/leaderboard/leaderboard.component').then(
            (c) => c.LeaderboardComponent,
          ),
      },
      {
        path: 'bot-management',
        loadComponent: () => import('./pages/bot-management/bot-management.component').then((c) => c.BotManagementComponent),
        canActivate: [botOperatorGuard],
        children: [
          { path: '', redirectTo: 'overview', pathMatch: 'full' },
          { path: 'overview', loadComponent: () => import('./pages/bot-management/operations-overview.component').then(c => c.OperationsOverviewComponent), canActivate: [botOperatorGuard] },
          { path: 'incidents', loadComponent: () => import('./pages/bot-management/incidents.component').then(c => c.IncidentsComponent), canActivate: [botOperatorGuard] },
          { path: 'guilds', loadComponent: () => import('./pages/bot-management/guild-health.component').then(c => c.GuildHealthComponent), canActivate: [botOperatorGuard] },
          { path: 'usage', loadComponent: () => import('./pages/bot-management/global-usage.component').then(c => c.GlobalUsageComponent), canActivate: [botOperatorGuard] },
        ],
      },
      {
        path: 'server-selection',
        loadComponent: () =>
          import('./pages/server-selection/server-selection.component').then(
            (c) => c.ServerSelectionComponent,
          ),
        canActivate: [serverSelectionGuard],
      },
      {
        path: 'dashboard',
        loadComponent: () =>
          import('./pages/dashboard/dashboard.component').then(
            (c) => c.DashboardComponent,
          ),
        canActivate: [guildGuard, settingsGuard],
      },
      {
        path: 'xp',
        loadComponent: () =>
          import('./pages/xp-config/xp-config.component').then(
            (c) => c.XpConfigComponent,
          ),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'xp' },
      },
      {
        path: 'xp/seasons',
        loadComponent: () =>
          import('./pages/season-config/season-config.component').then(
            (c) => c.SeasonConfigComponent,
          ),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'xp' },
      },
      {
        path: 'xp/audit',
        loadComponent: () => import('./pages/xp-audit/xp-audit.component').then((c) => c.XpAuditComponent),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'xp-audit' },
      },
      {
        path: 'vc-hubs',
        loadComponent: () =>
          import('./pages/vc-hubs/vc-hubs.component').then(
            (c) => c.VcHubsComponent,
          ),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'voice-hubs' },
      },
      {
        path: 'self-roles',
        loadComponent: () =>
          import('./pages/self-roles/self-roles.component').then(
            (c) => c.SelfRolesComponent,
          ),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'self-roles' },
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
      },
      {
        path: 'server-config/roles',
        loadComponent: () =>
          import('./pages/role-permissions/role-permissions.component').then(
            (c) => c.RolePermissionsComponent,
          ),
        canActivate: [guildGuard, ownerGuard],
      },
      {
        path: 'server-config/bot-identity',
        loadComponent: () => import('./pages/custom-bot-identity/custom-bot-identity.component').then((c) => c.CustomBotIdentityComponent),
        canActivate: [guildGuard, ownerGuard],
      },
      {
        path: 'xp/level-up-announcements',
        loadComponent: () => import('./pages/level-up-announcements/level-up-announcements.component').then((c) => c.LevelUpAnnouncementsComponent),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'xp-announcements' },
      },
      {
        path: 'diagnostics/permissions',
        loadComponent: () => import('./pages/permission-diagnostics/permission-diagnostics.component').then((c) => c.PermissionDiagnosticsComponent),
        canActivate: [guildGuard, moduleGuard], data: { module: 'diagnostics' },
      },
      ...(!environment.production ? [{
        path: 'dev',
        loadComponent: () => import('./pages/dev-tools/dev-tools.component').then((c) => c.DevToolsComponent),
        canActivate: [guildGuard, ownerGuard],
      }] : []),
      {
        path: 'analytics',
        loadComponent: () => import('./pages/guild-analytics/analytics-shell.component').then(c => c.AnalyticsShellComponent),
        canActivate: [guildGuard, moduleGuard],
        data: { module: 'analytics' },
        children: [
          { path: '', redirectTo: 'overview', pathMatch: 'full' },
          { path: 'overview', loadComponent: () => import('./pages/guild-analytics/analytics-overview.component').then(c => c.AnalyticsOverviewComponent), canActivate: [guildGuard, moduleGuard], data: { module: 'analytics' } },
          { path: 'xp', loadComponent: () => import('./pages/guild-analytics/analytics-xp.component').then(c => c.AnalyticsXpComponent), canActivate: [guildGuard, moduleGuard], data: { module: 'analytics' } },
          { path: 'voice', loadComponent: () => import('./pages/guild-analytics/analytics-voice.component').then(c => c.AnalyticsVoiceComponent), canActivate: [guildGuard, moduleGuard], data: { module: 'analytics' } },
          { path: 'features', loadComponent: () => import('./pages/guild-analytics/analytics-features.component').then(c => c.AnalyticsFeaturesComponent), canActivate: [guildGuard, moduleGuard], data: { module: 'analytics' } },
          { path: 'audit', loadComponent: () => import('./pages/guild-analytics/analytics-audit.component').then(c => c.AnalyticsAuditComponent), canActivate: [guildGuard, moduleGuard], data: { module: 'analytics' } },
        ],
      },
      { path: 'logs', redirectTo: '/analytics/audit', pathMatch: 'full' },
      { path: 'logs/activity', redirectTo: '/analytics/audit', pathMatch: 'full' },
      { path: 'logs/commands', redirectTo: '/analytics/features', pathMatch: 'full' },
      { path: 'logs/errors', redirectTo: '/analytics/audit', pathMatch: 'full' },
    ],
  },
  { path: '**', redirectTo: '' },
];
