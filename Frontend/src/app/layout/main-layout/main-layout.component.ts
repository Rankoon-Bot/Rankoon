import { Component, DestroyRef, effect, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import {
  NavigationCancel,
  NavigationEnd,
  NavigationError,
  NavigationSkipped,
  NavigationStart,
  Router,
  RouterOutlet,
} from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HeaderComponent } from '../header/header.component';
import { SidebarComponent } from '../sidebar/sidebar.component';
import { AuthStore } from '../../store/auth.store';
import { ModuleTranslationService } from '../../i18n/module-translation.service';
import { AppStore } from '../../store/app.store';
import { GuildAccessService } from '../../services/guild-access.service';
import { AuthService } from '../../services/auth.service';
import { LayoutStateService } from '../layout-state.service';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'app-main-layout',
  standalone: true,
  imports: [CommonModule, RouterOutlet, HeaderComponent, SidebarComponent, TranslocoPipe],
  template: `
    <div class="layout-container">
        <app-header></app-header>
        <div class="layout-content">
        <app-sidebar
          *ngIf="
            authStore.isAuthenticated() && translations.isLoaded('navigation')
          "
        ></app-sidebar>
        @if (layoutState.mobileNavigationOpen()) {
          <button class="navigation-backdrop" type="button" (click)="layoutState.closeMobileNavigation()" [attr.aria-label]="'header.closeNavigation' | transloco"></button>
        }
        <main
          id="main-content"
          class="main-content"
          [class.with-sidebar]="authStore.isAuthenticated()"
        >
          <router-outlet></router-outlet>
          @if (isNavigating()) {
            <div class="navigation-loading-overlay" aria-live="polite">
              <div class="navigation-loading-spinner" role="status" [attr.aria-label]="'common.loading' | transloco"></div>
            </div>
          }
        </main>
      </div>
    </div>
  `,
  styleUrls: ['./main-layout.component.scss'],
})
export class MainLayoutComponent {
  readonly authStore = inject(AuthStore);
  readonly translations = inject(ModuleTranslationService);
  readonly layoutState = inject(LayoutStateService);
  readonly isNavigating = signal(false);
  private readonly appStore = inject(AppStore);
  private readonly guildAccess = inject(GuildAccessService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  constructor() {
    this.router.events.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((event) => {
      if (event instanceof NavigationStart) {
        this.isNavigating.set(true);
      } else if (
        event instanceof NavigationEnd ||
        event instanceof NavigationCancel ||
        event instanceof NavigationError ||
        event instanceof NavigationSkipped
      ) {
        this.isNavigating.set(false);
      }
    });

    effect(() => {
      if (!this.authStore.isAuthenticated()) return;

      void this.translations.load('navigation');
      this.auth.refreshBotOperatorAccess();

      const guild = this.appStore.selectedGuild();
      if (!guild?.botInstalled) return;

      // Public ranking routes do not run a capability guard after a page reload.
      this.guildAccess.loadCapabilities(guild.id).subscribe({ error: () => undefined });
    });
    effect(() => {
      document.body.classList.toggle('rk-navigation-open', this.layoutState.mobileNavigationOpen());
    });
  }
}
