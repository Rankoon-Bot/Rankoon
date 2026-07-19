import { Component, input } from '@angular/core';

@Component({
  selector: 'app-report-kpi',
  standalone: true,
  template: `<article class="kpi"><span>{{ label() }}</span><strong>{{ value() }}</strong><small>{{ hint() }}</small></article>`,
  styles: [`
    .kpi { min-height: 128px; display: grid; align-content: center; gap: var(--rk-space-2); padding: var(--rk-space-5); background: var(--rk-surface-1); border: 1px solid var(--rk-border-subtle); border-radius: var(--rk-radius-lg); box-shadow: var(--rk-shadow-panel); }
    span { color: var(--rk-text-muted); font-size: .75rem; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; }
    strong { color: var(--rk-text-strong); font-size: 1.8rem; line-height: 1; }
    small { color: var(--rk-text-muted); }
  `]
})
export class ReportKpiComponent {
  readonly label = input.required<string>();
  readonly value = input.required<string | number>();
  readonly hint = input('');
}
