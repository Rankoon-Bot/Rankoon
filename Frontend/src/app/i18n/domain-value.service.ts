import { Injectable, inject } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';

@Injectable({ providedIn: 'root' })
export class DomainValueService {
  private readonly i18n = inject(TranslocoService);
  private readonly watchdogStates = ['starting', 'healthy', 'degraded', 'stale', 'restarting', 'faulted', 'stopped'];

  activityName(value: string): string { return this.translate('domain.reports.names', value); }
  activityAction(value: string): string { return this.translate('domain.reports.actions', value); }
  errorSource(value: string): string { return this.translate('domain.reports.errorSources', value); }
  outcome(value: string): string { return this.translate('domain.reports.outcomes', value); }
  severity(value: string): string { return this.translate('domain.reports.severities', value); }

  watchdogState(value: string | number | null | undefined): string {
    if (value === null || value === undefined || value === '') return this.i18n.translate('common.unknown');
    const numeric = typeof value === 'number' ? value : Number(value);
    const id = Number.isInteger(numeric) ? this.watchdogStates[numeric] : String(value).toLowerCase();
    return this.watchdogStates.includes(id) ? this.i18n.translate(`domain.watchdog.${id}`) : this.humanize(String(value));
  }

  private translate(prefix: string, value: string): string {
    const key = `${prefix}.${value}`;
    const translated = this.i18n.translate(key);
    if (translated !== key && typeof translated === 'string') return translated;
    const labelKey = `${key}.label`;
    const label = this.i18n.translate(labelKey);
    return label === labelKey ? this.humanize(value) : label;
  }

  private humanize(value: string): string {
    return value.split(/[._-]/).filter(Boolean).map(part => part.charAt(0).toLocaleUpperCase() + part.slice(1)).join(' ');
  }
}
