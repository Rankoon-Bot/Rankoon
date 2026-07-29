import { inject, Injectable } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { LocaleService } from '../../i18n/locale.service';
import { AnalyticsUnit } from './analytics-chart.models';

@Injectable({ providedIn: 'root' })
export class AnalyticsFormatterService {
  private readonly locale = inject(LocaleService);
  private readonly i18n = inject(TranslocoService);

  value(value: number | string, unit: AnalyticsUnit): string {
    const numeric = Number(value) || 0;
    if (unit === 'duration') return this.duration(numeric);
    if (unit === 'percent') return this.locale.number(numeric / 100, { style: 'percent', maximumFractionDigits: 2 });
    const number = this.locale.number(numeric, { maximumFractionDigits: unit === 'xp' ? 2 : 0 });
    return this.i18n.translate(`analytics.units.${unit}`, { value: number });
  }

  duration(seconds: number): string {
    const total = Math.max(0, Math.round(seconds));
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor(total % 3600 / 60);
    const remaining = total % 60;
    const parts: string[] = [];
    if (hours) parts.push(this.i18n.translate('analytics.duration.hours', { value: this.locale.number(hours) }));
    if (minutes || hours) parts.push(this.i18n.translate('analytics.duration.minutes', { value: this.locale.number(minutes) }));
    if (!hours && !minutes) parts.push(this.i18n.translate('analytics.duration.seconds', { value: this.locale.number(remaining) }));
    return parts.join(' ');
  }

  date(value: string, full = false, timeZone = 'UTC'): string {
    return this.locale.date(value, full
      ? { dateStyle: 'full', timeStyle: 'short', timeZone }
      : { month: 'short', day: 'numeric', timeZone });
  }

  dateTime(value: string, timeZone = 'UTC'): string {
    return this.locale.date(value, { dateStyle: 'short', timeStyle: 'short', timeZone });
  }

  percentRatio(value: number): string {
    if (value > 0 && value < 0.0001) return this.i18n.translate('analytics.lessThanPercent', { value: this.locale.number(0.01) });
    return this.locale.number(value, { style: 'percent', maximumFractionDigits: 2 });
  }
}
