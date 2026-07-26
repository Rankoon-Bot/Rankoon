import { Component, Input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

export interface SubNavigationItem { path: string; label: string; }

@Component({
  selector: 'rk-sub-navigation',
  standalone: true,
  imports: [RouterLink, RouterLinkActive],
  template: `<nav class="rk-sub-navigation" [attr.aria-label]="ariaLabel">@for (item of items; track item.path) { <a [routerLink]="item.path" routerLinkActive="is-active" [routerLinkActiveOptions]="{ exact: true }">{{ item.label }}</a> }</nav>`,
  styleUrl: './sub-navigation.component.scss',
})
export class SubNavigationComponent {
  @Input({ required: true }) items: readonly SubNavigationItem[] = [];
  @Input({ required: true }) ariaLabel = '';
}
