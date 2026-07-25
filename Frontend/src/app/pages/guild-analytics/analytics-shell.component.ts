import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({ selector: 'app-analytics-shell', standalone: true, imports: [RouterLink, RouterLinkActive, RouterOutlet, TranslocoPipe], template: `<div class="rk-page shell"><nav class="tabs" [attr.aria-label]="'analytics.navigation' | transloco">@for (page of pages; track page) { <a [routerLink]="page" routerLinkActive="active">{{ ('analytics.pages.' + page + '.short') | transloco }}</a> }</nav><router-outlet /></div>`, styles: [`
  .shell { display: grid; gap: var(--rk-space-5); }.tabs { display: flex; gap: var(--rk-space-1); overflow-x: auto; border-bottom: 1px solid var(--rk-border-subtle); }.tabs a { min-height: var(--rk-control-height); display: inline-flex; align-items: center; padding: 0 var(--rk-space-3); color: var(--rk-text-muted); border-bottom: var(--rk-space-1) solid transparent; white-space: nowrap; }.tabs a:hover { color: var(--rk-text-strong); }.tabs a.active { color: var(--rk-text-strong); border-color: var(--rk-brand); }
`] })
export class AnalyticsShellComponent { readonly pages = ['overview', 'xp', 'voice', 'features', 'audit'] as const; }
