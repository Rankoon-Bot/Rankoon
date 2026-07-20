import { Component, effect, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterOutlet } from '@angular/router';
import { HeaderComponent } from '../header/header.component';
import { SidebarComponent } from '../sidebar/sidebar.component';
import { AuthStore } from '../../store/auth.store';
import { ModuleTranslationService } from '../../i18n/module-translation.service';
import { AppStore } from '../../store/app.store';
import { GuildAccessService } from '../../services/guild-access.service';

@Component({
  selector: 'app-main-layout',
  standalone: true,
  imports: [CommonModule, RouterOutlet, HeaderComponent, SidebarComponent],
  template: `
    <div class="layout-container">
      <app-header></app-header>
      <div class="layout-content">
        <app-sidebar
          *ngIf="
            authStore.isAuthenticated() && translations.isLoaded('navigation')
          "
        ></app-sidebar>
        <main
          class="main-content"
          [class.with-sidebar]="authStore.isAuthenticated()"
        >
          <router-outlet></router-outlet>
        </main>
      </div>
    </div>
  `,
  styleUrls: ['./main-layout.component.scss'],
})
export class MainLayoutComponent {
  readonly authStore = inject(AuthStore);
  readonly translations = inject(ModuleTranslationService);
  private readonly appStore = inject(AppStore);
  private readonly guildAccess = inject(GuildAccessService);

  constructor() {
    effect(() => {
      if (!this.authStore.isAuthenticated()) return;

      void this.translations.load('navigation');

      const guild = this.appStore.selectedGuild();
      if (!guild?.botInstalled) return;

      // Public ranking routes do not run a capability guard after a page reload.
      this.guildAccess.loadCapabilities(guild.id).subscribe({ error: () => undefined });
    });
  }
}
