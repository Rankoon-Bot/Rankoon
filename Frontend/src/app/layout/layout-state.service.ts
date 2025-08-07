import { Injectable, signal } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class LayoutStateService {
  readonly mobileNavigationOpen = signal(false);

  toggleMobileNavigation(): void {
    this.mobileNavigationOpen.update(open => !open);
  }

  closeMobileNavigation(): void {
    this.mobileNavigationOpen.set(false);
  }
}
