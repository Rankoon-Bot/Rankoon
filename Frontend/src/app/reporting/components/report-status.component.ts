import { Component, input } from '@angular/core';
import { ReportStatus } from '../reporting.models';

@Component({
  selector: 'app-report-status',
  standalone: true,
  template: `<span class="status" [class]="'status ' + status()"><span aria-hidden="true" class="dot"></span>{{ label() }}</span>`,
  styles: [`
    .status { display: inline-flex; align-items: center; gap: var(--rk-space-2); width: max-content; padding: var(--rk-space-1) var(--rk-space-2); color: var(--rk-text); background: var(--rk-surface-2); border-radius: var(--rk-radius-sm); font-size: .75rem; font-weight: 700; }
    .dot { width: 7px; height: 7px; border-radius: 50%; background: var(--rk-text-muted); }
    .success { color: var(--rk-success); background: var(--rk-success-subtle); } .success .dot { background: var(--rk-success); }
    .warning { color: var(--rk-warning); background: var(--rk-warning-subtle); } .warning .dot { background: var(--rk-warning); }
    .error { color: var(--rk-danger); background: var(--rk-danger-subtle); } .error .dot { background: var(--rk-danger); }
    .info { color: var(--rk-info); background: var(--rk-info-subtle); } .info .dot { background: var(--rk-info); }
  `]
})
export class ReportStatusComponent {
  readonly status = input<ReportStatus>('neutral');
  readonly label = input.required<string>();
}
