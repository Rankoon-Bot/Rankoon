import { Component, computed, inject, input } from '@angular/core';
import { TrendPointDto } from '../reporting.models';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { LocaleService } from '../../i18n/locale.service';

@Component({
  selector: 'app-report-trend-chart',
  standalone: true,
  imports: [TranslocoPipe],
  template: `
    <div class="chart" role="img" [attr.aria-label]="accessibleLabel()">
      @if (points().length > 1) {
        <svg viewBox="0 0 600 180" preserveAspectRatio="none" aria-hidden="true" focusable="false">
          <line x1="0" y1="45" x2="600" y2="45"/><line x1="0" y1="90" x2="600" y2="90"/><line x1="0" y1="135" x2="600" y2="135"/>
          <polyline [attr.points]="polyline()" />
        </svg>
        <div class="axis"><span>{{ firstLabel() }}</span><span>{{ lastLabel() }}</span></div>
      } @else { <p>{{ 'reports.notEnoughTrend' | transloco }}</p> }
    </div>
  `,
  styles: [`
    .chart { min-height: 220px; display: grid; align-content: center; }
    svg { width: 100%; height: 180px; overflow: visible; }
    line { stroke: var(--rk-border-subtle); stroke-width: 1; vector-effect: non-scaling-stroke; }
    polyline { fill: none; stroke: var(--rk-info); stroke-width: 3; stroke-linecap: round; stroke-linejoin: round; vector-effect: non-scaling-stroke; }
    .axis { display: flex; justify-content: space-between; color: var(--rk-text-muted); font-size: .75rem; }
    p { color: var(--rk-text-muted); text-align: center; }
  `]
})
export class ReportTrendChartComponent {
  private readonly locale = inject(LocaleService);
  private readonly i18n = inject(TranslocoService);
  readonly points = input<TrendPointDto[]>([]);
  readonly polyline = computed(() => {
    const data = this.points();
    const max = Math.max(...data.map(point => point.value), 1);
    return data.map((point, index) => `${index * (600 / Math.max(data.length - 1, 1))},${170 - (point.value / max) * 155}`).join(' ');
  });
  readonly accessibleLabel = computed(() => this.i18n.translate('reports.trendAria', { count: this.points().length, values: this.points().map(point => point.value).join(', ') }));
  readonly firstLabel = computed(() => this.formatDate(this.points()[0]?.timestamp));
  readonly lastLabel = computed(() => this.formatDate(this.points().at(-1)?.timestamp));

  private formatDate(value?: string): string {
    return value ? this.locale.date(value, { day: '2-digit', month: 'short' }) : '';
  }
}
