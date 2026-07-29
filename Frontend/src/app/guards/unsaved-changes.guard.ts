import { CanDeactivateFn } from '@angular/router';

export interface DirtyRouteComponent {
  canDeactivate(): boolean;
}

export const unsavedChangesGuard: CanDeactivateFn<DirtyRouteComponent> = component => component.canDeactivate();
