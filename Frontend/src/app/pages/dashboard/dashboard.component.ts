import { Component, effect, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { finalize } from 'rxjs';
import { AppStore } from '../../store/app.store';
import { LocaleService } from '../../i18n/locale.service';
import { ApiErrorService } from '../../services/api-error.service';
import { DashboardService } from '../../services/dashboard.service';
import { DashboardOverview, DashboardPeriod, DashboardStatus } from '../../models/dashboard.models';

const ROUTES: Record<string, string> = { xp: '/xp', seasons: '/xp/seasons', 'level-up-announcements': '/xp/level-up-announcements', leaderboard: '/server-config/leaderboard', 'xp-audit': '/xp/audit', 'voice-hubs': '/vc-hubs', 'self-roles': '/self-roles', analytics: '/analytics/overview', reporting: '/analytics/overview', diagnostics: '/diagnostics/permissions', 'dashboard-access': '/server-config/roles', 'bot-identity': '/server-config/bot-identity' };

@Component({ selector: 'app-dashboard', standalone: true, imports: [CommonModule, RouterLink, TranslocoPipe], templateUrl: './dashboard.component.html', styleUrls: ['./dashboard.component.scss'] })
export class DashboardComponent {
  readonly appStore = inject(AppStore); private readonly api = inject(DashboardService); private readonly locale = inject(LocaleService); private readonly apiErrors = inject(ApiErrorService);
  readonly data = signal<DashboardOverview | null>(null); readonly loading = signal(false); readonly error = signal(''); readonly period = signal<DashboardPeriod>('SevenDays');
  private guildId: string | null = null; private requestId = 0;
  constructor() { effect(() => { const id = this.appStore.selectedGuild()?.id ?? null; if (id !== this.guildId) { this.guildId = id; id ? this.load(id) : this.data.set(null); } }); }
  load(guildId = this.guildId): void { if (!guildId) return; const request = ++this.requestId; this.loading.set(true); this.error.set(''); this.api.dashboard(guildId, this.period()).pipe(finalize(() => { if (request === this.requestId) this.loading.set(false); })).subscribe({ next: data => { if (request === this.requestId) this.data.set(data); }, error: error => { if (request === this.requestId) this.error.set(this.apiErrors.resolve(error, 'errors.dashboardLoad').message); } }); }
  selectPeriod(period: DashboardPeriod): void { if (period !== this.period()) { this.period.set(period); this.load(); } }
  format(value: number | string | null | undefined): string { return value == null ? '-' : this.locale.number(value); }
  voice(seconds: number): string { const h = Math.floor(seconds / 3600); const m = Math.floor((seconds % 3600) / 60); return `${h}h ${m}m`; }
  status(status: DashboardStatus): string { return `dashboard.status.${status}`; }
  route(moduleId: string): string { return ROUTES[moduleId] ?? '/'; }
  hasModule(overview: DashboardOverview, moduleId: string): boolean { return overview.modules.some(module => module.moduleId === moduleId || moduleId === 'analytics' && module.moduleId === 'reporting'); }
  sourceKey(source: string): string { return `dashboard.sources.${source}`; }
  eventKey(name: string): string { return `domain.reports.names.${name.replaceAll('.', '.')}`; }
  chartPoints(points: { xpAwarded: number }[]): string {
    const maximum = Math.max(...points.map(point => point.xpAwarded), 1);
    return points.map((point, index) => `${index * 100 / Math.max(points.length - 1, 1)},${38 - point.xpAwarded * 34 / maximum}`).join(' ');
  }
}
