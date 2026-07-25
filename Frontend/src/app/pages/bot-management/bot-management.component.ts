import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({ selector: 'app-bot-management', standalone: true, imports: [RouterLink, RouterLinkActive, RouterOutlet, TranslocoPipe], templateUrl: './bot-management.component.html', styleUrl: './bot-management.component.scss' })
export class BotManagementComponent {
  readonly pages = ['overview', 'incidents', 'guilds', 'usage'] as const;
}
