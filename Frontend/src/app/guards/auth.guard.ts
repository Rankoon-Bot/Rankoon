import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthStore } from '../store/auth.store';
import { AppStore } from '../store/app.store';

export const authGuard: CanActivateFn = (route, state) => {
  const authStore = inject(AuthStore);
  const router = inject(Router);

  if (authStore.isAuthenticated()) {
    return true;
  }

  // Redirect to login page with return url
  router.navigate(['/login'], { queryParams: { returnUrl: state.url } });
  return false;
};

export const guildGuard: CanActivateFn = (route, state) => {
  const authStore = inject(AuthStore);
  const appStore = inject(AppStore);
  const router = inject(Router);

  // First check authentication
  if (!authStore.isAuthenticated()) {
    return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  }

  // Server-specific pages require a current selection where the bot is installed.
  if (appStore.selectedGuild()?.botInstalled === true) {
    return true;
  }

  appStore.setSelectedGuild(null);
  return router.createUrlTree(['/server-selection']);
};

export const serverSelectionGuard: CanActivateFn = (route, state) => {
  const authStore = inject(AuthStore);
  const router = inject(Router);

  // First check authentication
  if (!authStore.isAuthenticated()) {
    router.navigate(['/login'], { queryParams: { returnUrl: state.url } });
    return false;
  }

  // Allow access to server selection for authenticated users
  // (even if they already have a guild selected - they might want to change it)
  return true;
};

export const guestGuard: CanActivateFn = (route, state) => {
  const authStore = inject(AuthStore);
  const router = inject(Router);

  if (!authStore.isAuthenticated()) {
    return true;
  }

  // Redirect to server selection if already authenticated
  router.navigate(['/server-selection']);
  return false;
};
