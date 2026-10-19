import { LoadingSkeletonComponent } from '../../shared/ui/loading-skeleton/loading-skeleton.component';
import { Component, input, output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'app-report-state',
  standalone: true,
  imports: [TranslocoPipe, LoadingSkeletonComponent],
  template: `
    @if (loading()) { <rk-loading-skeleton layout="table" /> } @else {
    <section class="state" role="status">
      <div class="mascot-slot" aria-hidden="true"></div>
      @if (error()) { <h2>{{ 'reports.unavailable' | transloco }}</h2><p>{{ error() }}</p><button class="rk-button" type="button" (click)="retry.emit()">{{ 'common.retry' | transloco }}</button> }
      @else { <h2>{{ 'reports.empty' | transloco }}</h2><p>{{ emptyMessage() || ('reports.emptyDefault' | transloco) }}</p> }
    </section>
    }
  `,
  styles: [`
    .state { min-height: 260px; display: grid; place-items: center; align-content: center; gap: var(--rk-space-3); padding: var(--rk-space-8); color: var(--rk-text-muted); background: var(--rk-surface-1); border: 1px solid var(--rk-border-subtle); border-radius: var(--rk-radius-lg); text-align: center; }
    .mascot-slot { min-height: var(--rk-space-1); }
    h2 { font-size: 1.1rem; }

  `]
})
export class ReportStateComponent {
  readonly loading = input(false);
  readonly error = input<string | null>(null);
  readonly emptyMessage = input('');
  readonly retry = output<void>();
}
