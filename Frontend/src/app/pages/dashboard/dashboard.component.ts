import { Component, computed, effect, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { finalize } from 'rxjs';
import { AppStore } from '../../store/app.store';
import { LocaleService } from '../../i18n/locale.service';
import { ApiErrorService } from '../../services/api-error.service';
import { DashboardService } from '../../services/dashboard.service';
import { DashboardOverview, DashboardPeriod, DashboardStatus } from '../../models/dashboard.models';
import { AnalyticsLineChartComponent } from '../../shared/analytics-chart/analytics-line-chart.component';
import { AnalyticsChartPoint, AnalyticsChartSeries, AnalyticsUnit } from '../../shared/analytics-chart/analytics-chart.models';
import { AnalyticsFormatterService } from '../../shared/analytics-chart/analytics-formatter.service';

type DashboardMetric = 'xp' | 'voice' | 'activities' | 'members';

const ROUTES: Record<string, string> = { xp: '/xp/settings', seasons: '/xp/seasons', 'level-up-announcements': '/xp/level-up-announcements', leaderboard: '/server-config/leaderboard', 'xp-audit': '/xp/audit', 'voice-hubs': '/vc-hubs', 'self-roles': '/self-roles', analytics: '/analytics/overview', reporting: '/analytics/overview', diagnostics: '/diagnostics/permissions', 'dashboard-access': '/server-config/roles', 'bot-identity': '/server-config/bot-identity' };

@Component({ selector: 'app-dashboard', standalone: true, imports: [CommonModule, RouterLink, TranslocoPipe, AnalyticsLineChartComponent], templateUrl: './dashboard.component.html', styleUrls: ['./dashboard.component.scss'] })
export class DashboardComponent {
  readonly appStore = inject(AppStore); private readonly api = inject(DashboardService); readonly locale = inject(LocaleService); readonly analyticsFormatter = inject(AnalyticsFormatterService); private readonly apiErrors = inject(ApiErrorService); private readonly i18n = inject(TranslocoService);
  readonly data = signal<DashboardOverview | null>(null); readonly loading = signal(false); readonly error = signal(''); readonly period = signal<DashboardPeriod>('SevenDays');
  readonly metric = signal<DashboardMetric>('xp');
  readonly chartSeries = computed<AnalyticsChartSeries[]>(() => { this.locale.locale(); return [{ key: this.metric(), label: this.i18n.translate(this.metricLabel(this.metric())) }]; });
  readonly chartPoints = computed<AnalyticsChartPoint[]>(() => (this.data()?.activity.trend ?? []).map(point => ({ timestamp: point.dateUtc, isIncomplete: this.isToday(point.dateUtc), values: { [this.metric()]: this.metricValue(point, this.metric()) } })));
  readonly sources = computed(() => {
    const source = this.data()?.activity.sources ?? [];
    const grouped = ['voice', 'message', 'reaction', 'other'].map(key => {
      const rows = key === 'other' ? source.filter(item => !['voice', 'message', 'reaction'].includes(item.source)) : source.filter(item => item.source === key);
      return { source: key, xpAwarded: rows.reduce((sum, item) => sum + Number(item.xpAwarded), 0), eventCount: rows.reduce((sum, item) => sum + Number(item.eventCount), 0) };
    });
    const total = grouped.reduce((sum, item) => sum + item.xpAwarded, 0);
    return grouped.map(item => ({ ...item, ratio: total ? item.xpAwarded / total : 0 }));
  });
  private guildId: string | null = null; private requestId = 0;
  constructor() { effect(() => { const id = this.appStore.selectedGuild()?.id ?? null; if (id !== this.guildId) { this.guildId = id; id ? this.load(id) : this.data.set(null); } }); }
  load(guildId = this.guildId): void { if (!guildId) return; const request = ++this.requestId; this.loading.set(true); this.error.set(''); this.api.dashboard(guildId, this.period()).pipe(finalize(() => { if (request === this.requestId) this.loading.set(false); })).subscribe({ next: data => { if (request === this.requestId) this.data.set(data); }, error: error => { if (request === this.requestId) this.error.set(this.apiErrors.resolve(error, 'errors.dashboardLoad').message); } }); }
  selectPeriod(period: DashboardPeriod): void { if (period !== this.period()) { this.period.set(period); this.load(); } }
  format(value: number | string | null | undefined): string { return value == null ? '-' : this.locale.number(value); }
  voice(seconds: number): string { return this.analyticsFormatter.duration(seconds); }
  status(status: DashboardStatus): string { return `dashboard.status.${status}`; }
  route(moduleId: string): string { return ROUTES[moduleId] ?? '/'; }
  hasModule(overview: DashboardOverview, moduleId: string): boolean { return overview.modules.some(module => module.moduleId === moduleId || moduleId === 'analytics' && module.moduleId === 'reporting'); }
  sourceKey(source: string): string { return `dashboard.sources.${source}`; }
  eventKey(name: string): string { return `domain.reports.names.${name.replaceAll('.', '.')}`; }
  selectMetric(metric: DashboardMetric): void { this.metric.set(metric); }
  metricLabel(metric: DashboardMetric): string { return `dashboard.metrics.${metric}`; }
  metricUnit(): AnalyticsUnit { return this.metric() === 'xp' ? 'xp' : this.metric() === 'voice' ? 'duration' : this.metric() === 'members' ? 'members' : 'count'; }
  metricValue(point: DashboardOverview['activity']['trend'][number], metric: DashboardMetric): number { return Number(metric === 'xp' ? point.xpAwarded : metric === 'voice' ? point.voiceSeconds : metric === 'members' ? point.activeMemberCount : point.qualifiedActivityCount); }
  private isToday(value: string): boolean { const date = new Date(value); const now = new Date(); return date.getUTCFullYear() === now.getUTCFullYear() && date.getUTCMonth() === now.getUTCMonth() && date.getUTCDate() === now.getUTCDate(); }
}
