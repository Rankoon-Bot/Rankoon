import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { TranslocoService } from '@jsverse/transloco';
import { inject } from '@angular/core';
import { SubNavigationComponent } from '../../shared/ui/sub-navigation/sub-navigation.component';

@Component({ selector: 'app-analytics-shell', standalone: true, imports: [RouterOutlet, TranslocoPipe, SubNavigationComponent], template: `<div class="rk-page shell"><rk-sub-navigation [items]="items()" [ariaLabel]="'analytics.navigation' | transloco" /><router-outlet /></div>`, styles: [`.shell { display: grid; gap: var(--rk-space-5); }`] })
export class AnalyticsShellComponent {
  private readonly i18n = inject(TranslocoService);
  items = () => ['overview', 'xp', 'voice', 'features', 'audit'].map(path => ({ path, label: this.i18n.translate(`analytics.pages.${path}.short`) }));
}
