import { Routes } from '@angular/router';
import { authGuard, guestGuard } from './guards/auth.guard';
import { MainLayoutComponent } from './layout/main-layout/main-layout.component';

export const routes: Routes = [
  {
    path: '',
    component: MainLayoutComponent,
    children: [
      {
        path: '',
        redirectTo: '/dashboard',
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
        path: 'dashboard',
        loadComponent: () => import('./pages/dashboard/dashboard.component').then(c => c.DashboardComponent),
        canActivate: [authGuard]
      },
      {
        path: 'server-config',
        canActivate: [authGuard],
        loadComponent: () => import('./pages/dashboard/dashboard.component').then(c => c.DashboardComponent) // Placeholder
      },
      {
        path: 'moderation',
        canActivate: [authGuard],
        loadComponent: () => import('./pages/dashboard/dashboard.component').then(c => c.DashboardComponent) // Placeholder
      },
      {
        path: 'economy',
        canActivate: [authGuard],
        loadComponent: () => import('./pages/dashboard/dashboard.component').then(c => c.DashboardComponent) // Placeholder
      },
      {
        path: 'fun',
        canActivate: [authGuard],
        loadComponent: () => import('./pages/dashboard/dashboard.component').then(c => c.DashboardComponent) // Placeholder
      },
      {
        path: 'logs',
        canActivate: [authGuard],
        loadComponent: () => import('./pages/dashboard/dashboard.component').then(c => c.DashboardComponent) // Placeholder
      }
    ]
  },
  {
    path: '**',
    redirectTo: '/dashboard'
  }
];
