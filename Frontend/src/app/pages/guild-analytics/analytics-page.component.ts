import { CommonModule } from '@angular/common';
import { Component, computed, inject, input, output } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { LocaleService } from '../../i18n/locale.service';
import { ANALYTICS_RANGES, AnalyticsRange, GuildAnalyticsResponse } from './guild-analytics.models';

@Component({
  selector: 'app-analytics-page', standalone: true, imports: [CommonModule, TranslocoPipe],
  templateUrl: './analytics-page.component.html', styleUrl: './analytics-page.component.scss'
})
export class AnalyticsPageComponent {
  private readonly i18n = inject(TranslocoService);
  readonly locale = inject(LocaleService);
  readonly page = input.required<string>();
  readonly range = input.required<AnalyticsRange>();
  readonly data = input<GuildAnalyticsResponse | null>(null);
  readonly loading = input(false);
  readonly error = input<string | null>(null);
  readonly rangeChange = output<AnalyticsRange>();
  readonly retry = output<void>();
  readonly ranges = ANALYTICS_RANGES;
  readonly kpis = computed(() => (this.data()?.kpis ?? []).slice(0, 4));
  readonly maxTrend = computed(() => Math.max(1, ...(this.data()?.trend ?? []).map(point => point.value)));

  label(kind: 'kpis' | 'breakdowns' | 'insights' | 'audit', code: string): string {
    const key = `analytics.domain.${kind}.${code}`;
    const translated = this.i18n.translate(key);
    return translated === key ? code : translated;
  }

  insight(code: string, context: Record<string, unknown>): string {
    const key = `analytics.domain.insightText.${code}`;
    const translated = this.i18n.translate(key, context);
    return translated === key ? code : translated;
  }
}
