import { Routes } from '@angular/router';
import { authGuard, guildGuard, guestGuard, serverSelectionGuard } from './guards/auth.guard';
import { MainLayoutComponent } from './layout/main-layout/main-layout.component';

export const routes: Routes = [
  {
    path: '',
    component: MainLayoutComponent,
    children: [
      {
        path: '',
        redirectTo: '/server-selection',
        pathMatch: 'full'
      },
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
        path: 'server-selection',
        loadComponent: () => import('./pages/server-selection/server-selection.component').then(c => c.ServerSelectionComponent),
        canActivate: [serverSelectionGuard]
      },
      {
        path: 'dashboard',
        loadComponent: () => import('./pages/dashboard/dashboard.component').then(c => c.DashboardComponent),
        canActivate: [guildGuard]
      },
      {
        path: 'xp',
        loadComponent: () => import('./pages/xp-config/xp-config.component').then(c => c.XpConfigComponent),
        canActivate: [guildGuard]
      },
      {
        path: 'vc-hubs',
        loadComponent: () => import('./pages/vc-hubs/vc-hubs.component').then(c => c.VcHubsComponent),
        canActivate: [guildGuard]
      },
      {
        path: 'server-config',
        canActivate: [guildGuard],
        loadComponent: () => import('./pages/dashboard/dashboard.component').then(c => c.DashboardComponent) // Placeholder
      },
      {
        path: 'moderation',
        canActivate: [guildGuard],
        loadComponent: () => import('./pages/dashboard/dashboard.component').then(c => c.DashboardComponent) // Placeholder
      },
      {
        path: 'economy',
        canActivate: [guildGuard],
        loadComponent: () => import('./pages/dashboard/dashboard.component').then(c => c.DashboardComponent) // Placeholder
      },
      {
        path: 'fun',
        canActivate: [guildGuard],
        loadComponent: () => import('./pages/dashboard/dashboard.component').then(c => c.DashboardComponent) // Placeholder
      },
      {
        path: 'logs',
        canActivate: [guildGuard],
        loadComponent: () => import('./pages/dashboard/dashboard.component').then(c => c.DashboardComponent) // Placeholder
      }
    ]
  },
  {
    path: '**',
    redirectTo: '/server-selection'
  }
];
