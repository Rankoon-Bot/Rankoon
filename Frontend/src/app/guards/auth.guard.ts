import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthStore } from '../store/auth.store';
import { AppStore } from '../store/app.store';
import { catchError, map, of } from 'rxjs';
import { GuildAccessService } from '../services/guild-access.service';
import { GuildModuleId } from '../models/guild-permissions.models';

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

function capabilityGuardFor(kind: 'settings' | 'module' | 'owner'): CanActivateFn {
  return (route) => {
    const appStore = inject(AppStore);
    const access = inject(GuildAccessService);
    const router = inject(Router);
    const guild = appStore.selectedGuild();
    if (!guild?.botInstalled) return router.createUrlTree(['/server-selection']);

    const requiredModule = route.data['module'] as GuildModuleId | undefined;
    return access.loadCapabilities(guild.id, true).pipe(
      map(capabilities => {
        const allowed = kind === 'owner'
          ? capabilities.isOwner
          : kind === 'module'
            ? capabilities.isOwner || (!!requiredModule && capabilities.moduleIds.includes(requiredModule))
            : capabilities.isOwner || capabilities.canAccessSettings;
        return allowed ? true : access.destination(capabilities);
      }),
      catchError(error => of(router.createUrlTree(['/server-selection'], {
        queryParams: { access: error?.status === 403 ? 'forbidden' : 'unavailable' }
      })))
    );
  };
}

export const settingsGuard = capabilityGuardFor('settings');
export const moduleGuard = capabilityGuardFor('module');
export const ownerGuard = capabilityGuardFor('owner');

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
