import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { inject } from '@angular/core';
import { SubNavigationComponent } from '../../shared/ui/sub-navigation/sub-navigation.component';

@Component({ selector: 'app-bot-management', standalone: true, imports: [RouterOutlet, TranslocoPipe, SubNavigationComponent], templateUrl: './bot-management.component.html', styleUrl: './bot-management.component.scss' })
export class BotManagementComponent {
  private readonly i18n = inject(TranslocoService);
  items = () => ['overview', 'incidents', 'guilds', 'usage'].map(path => ({ path, label: this.i18n.translate(`botManagement.pages.${path}`) }));
}
