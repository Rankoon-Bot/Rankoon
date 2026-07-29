import { Component, computed, inject, input, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { AnalyticsChartPoint, AnalyticsChartSeries, AnalyticsUnit } from './analytics-chart.models';
import { AnalyticsFormatterService } from './analytics-formatter.service';

interface PlotPoint { point: AnalyticsChartPoint; x: number; values: Record<string, number>; }

@Component({
  selector: 'app-analytics-line-chart',
  standalone: true,
  imports: [TranslocoPipe],
  templateUrl: './analytics-line-chart.component.html',
  styleUrl: './analytics-line-chart.component.scss'
})
export class AnalyticsLineChartComponent {
  readonly points = input.required<readonly AnalyticsChartPoint[]>();
  readonly series = input.required<readonly AnalyticsChartSeries[]>();
  readonly unit = input.required<AnalyticsUnit>();
  readonly xAxisLabel = input.required<string>();
  readonly yAxisLabel = input.required<string>();
  readonly timeZone = input('UTC');
  readonly loading = input(false);
  readonly error = input<string | null>(null);
  readonly showDelta = input(true);
  readonly formatter = inject(AnalyticsFormatterService);
  readonly selectedIndex = signal<number | null>(null);
  readonly hidden = signal<ReadonlySet<string>>(new Set());
  readonly visibleSeries = computed(() => this.series().filter(item => !this.hidden().has(item.key)));
  readonly plot = computed<PlotPoint[]>(() => {
    const source = this.points();
    return source.map((point, index) => ({
      point,
      x: 72 + index * 888 / Math.max(source.length - 1, 1),
      values: Object.fromEntries(this.series().map(item => [item.key, Number(point.values[item.key]) || 0]))
    }));
  });
  readonly maximum = computed(() => Math.max(1, ...this.plot().flatMap(item => this.visibleSeries().map(series => item.values[series.key]))));
  readonly ticks = computed(() => Array.from({ length: 5 }, (_, index) => this.maximum() * (4 - index) / 4));
  readonly xTicks = computed(() => {
    const length = this.plot().length;
    if (!length) return [];
    const count = Math.min(5, length);
    return Array.from(new Set(Array.from({ length: count }, (_, index) => Math.round(index * (length - 1) / Math.max(count - 1, 1)))));
  });
  readonly selected = computed(() => {
    const index = this.selectedIndex();
    return index === null ? null : this.plot()[index] ?? null;
  });

  path(key: string): string {
    return this.plot().map((item, index) => `${index ? 'L' : 'M'} ${item.x} ${this.y(item.values[key])}`).join(' ');
  }

  y(value: number): number { return 24 + 256 * (1 - value / this.maximum()); }
  seriesClass(series: AnalyticsChartSeries, index: number): string { return series.className || `series-${index % 4}`; }
  select(index: number): void { this.selectedIndex.set(index); }
  clear(): void { this.selectedIndex.set(null); }
  toggle(key: string): void {
    const next = new Set(this.hidden());
    next.has(key) ? next.delete(key) : next.add(key);
    if (next.size === this.series().length) next.delete(key);
    this.hidden.set(next);
  }
  pointer(event: PointerEvent): void {
    const rect = (event.currentTarget as SVGElement).getBoundingClientRect();
    const relative = Math.min(1, Math.max(0, ((event.clientX - rect.left) / rect.width - 0.072) / 0.888));
    this.select(Math.round(relative * Math.max(this.points().length - 1, 0)));
  }
  key(event: KeyboardEvent, index: number): void {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight' && event.key !== 'Home' && event.key !== 'End') return;
    event.preventDefault();
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? this.points().length - 1 : Math.min(this.points().length - 1, Math.max(0, index + (event.key === 'ArrowLeft' ? -1 : 1)));
    this.select(next);
  }
  delta(key: string): number | null {
    const index = this.selectedIndex();
    if (index === null || index === 0) return null;
    return (this.plot()[index]?.values[key] ?? 0) - (this.plot()[index - 1]?.values[key] ?? 0);
  }
}
