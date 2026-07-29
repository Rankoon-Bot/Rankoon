import { Injectable, inject } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { LocaleService } from '../i18n/locale.service';

type DurationUnit = 'month' | 'week' | 'day' | 'hour' | 'minute' | 'second';

const UNITS: ReadonlyArray<{ unit: DurationUnit; seconds: number }> = [
  { unit: 'month', seconds: 30 * 24 * 60 * 60 },
  { unit: 'week', seconds: 7 * 24 * 60 * 60 },
  { unit: 'day', seconds: 24 * 60 * 60 },
  { unit: 'hour', seconds: 60 * 60 },
  { unit: 'minute', seconds: 60 },
  { unit: 'second', seconds: 1 },
];

@Injectable({ providedIn: 'root' })
export class DurationFormatterService {
  private readonly i18n = inject(TranslocoService);
  private readonly locale = inject(LocaleService);

  compact(value: string | number): string {
    return this.parts(value).slice(0, 2).map(part =>
      this.i18n.translate(`duration.compact.${part.unit}`, { count: this.locale.number(part.value) }),
    ).join(' ');
  }

  full(value: string | number): string {
    const parts = this.parts(value).map(part =>
      this.locale.plural(part.value, `duration.full.${part.unit}One`, `duration.full.${part.unit}Other`),
    );
    return new Intl.ListFormat(this.locale.locale(), { style: 'long', type: 'conjunction' }).format(parts);
  }

  private parts(value: string | number): Array<{ unit: DurationUnit; value: number }> {
    const numeric = Number(value);
    let remaining = Number.isFinite(numeric) ? Math.max(0, Math.floor(numeric)) : 0;
    const parts: Array<{ unit: DurationUnit; value: number }> = [];
    for (const definition of UNITS) {
      const count = Math.floor(remaining / definition.seconds);
      if (count > 0) {
        parts.push({ unit: definition.unit, value: count });
        remaining %= definition.seconds;
      }
    }
    return parts.length > 0 ? parts : [{ unit: 'second', value: 0 }];
  }
}
