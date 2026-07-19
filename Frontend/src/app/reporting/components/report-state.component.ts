import { Component, input, output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'app-report-state',
  standalone: true,
  imports: [TranslocoPipe],
  template: `
    <section class="state" role="status">
      <div class="mascot-slot" aria-hidden="true"></div>
      @if (loading()) { <span class="spinner" aria-hidden="true"></span><h2>{{ 'reports.loading' | transloco }}</h2><p>{{ 'reports.loadingHint' | transloco }}</p> }
      @else if (error()) { <h2>{{ 'reports.unavailable' | transloco }}</h2><p>{{ error() }}</p><button type="button" (click)="retry.emit()">{{ 'common.retry' | transloco }}</button> }
      @else { <h2>{{ 'reports.empty' | transloco }}</h2><p>{{ emptyMessage() || ('reports.emptyDefault' | transloco) }}</p> }
    </section>
  `,
  styles: [`
    .state { min-height: 260px; display: grid; place-items: center; align-content: center; gap: var(--rk-space-3); padding: var(--rk-space-8); color: var(--rk-text-muted); background: var(--rk-surface-1); border: 1px solid var(--rk-border-subtle); border-radius: var(--rk-radius-lg); text-align: center; }
    .mascot-slot { min-height: var(--rk-space-1); }
    h2 { font-size: 1.1rem; }
    button { min-height: 44px; padding: 0 var(--rk-space-4); color: var(--rk-text-strong); background: var(--rk-surface-2); border: 1px solid var(--rk-border-strong); border-radius: var(--rk-radius-md); font-weight: 700; }
    button:hover { border-color: var(--rk-info); background: var(--rk-surface-3); }
    .spinner { width: 28px; height: 28px; border: 3px solid var(--rk-border-strong); border-top-color: var(--rk-info); border-radius: 50%; animation: spin var(--rk-motion-base) infinite; }
    @keyframes spin { to { transform: rotate(360deg); } }
  `]
})
export class ReportStateComponent {
  readonly loading = input(false);
  readonly error = input<string | null>(null);
  readonly emptyMessage = input('');
  readonly retry = output<void>();
}
