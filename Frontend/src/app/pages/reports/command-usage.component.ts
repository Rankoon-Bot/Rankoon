import { CommonModule } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { catchError, combineLatest, EMPTY, finalize, switchMap, tap } from 'rxjs';
import { ReportFilterBarComponent, ReportFilterValue } from '../../reporting/components/report-filter-bar.component';
import { ReportKpiComponent } from '../../reporting/components/report-kpi.component';
import { ReportStateComponent } from '../../reporting/components/report-state.component';
import { ReportStatusComponent } from '../../reporting/components/report-status.component';
import { ReportTrendChartComponent } from '../../reporting/components/report-trend-chart.component';
import { CommandQuery, CommandReportDto } from '../../reporting/reporting.models';
import { ReportingService } from '../../reporting/reporting.service';
import { AppStore } from '../../store/app.store';

@Component({
  selector: 'app-command-usage', standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive, ReportFilterBarComponent, ReportKpiComponent, ReportStateComponent, ReportStatusComponent, ReportTrendChartComponent],
  templateUrl: './command-usage.component.html', styleUrl: './command-usage.component.scss'
})
export class CommandUsageComponent {
  private readonly appStore = inject(AppStore);
  private readonly reporting = inject(ReportingService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly reloadTick = signal(0);
  private requestGeneration = 0;
  readonly report = signal<CommandReportDto | null>(null);
  readonly query = signal<CommandQuery>({ take: 25 });
  readonly loading = signal(true);
  readonly loadingMore = signal(false);
  readonly loadMoreError = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  constructor() {
    combineLatest([toObservable(this.appStore.selectedGuild), this.route.queryParamMap, toObservable(this.reloadTick)]).pipe(
      tap(([, params]) => { this.requestGeneration++; this.query.set(this.readQuery(params)); this.report.set(null); this.loading.set(true); this.error.set(null); this.loadMoreError.set(null); }),
      switchMap(([guild]) => {
        const generation = this.requestGeneration;
        if (!guild) { this.loading.set(false); return EMPTY; }
        return this.reporting.commands(guild.id, this.query()).pipe(
          catchError(() => { if (generation === this.requestGeneration) this.error.set('Die Command-Statistiken konnten nicht geladen werden.'); return EMPTY; }),
          finalize(() => { if (generation === this.requestGeneration) this.loading.set(false); })
        );
      }), takeUntilDestroyed(this.destroyRef)
    ).subscribe(report => this.report.set(report));
  }

  applyFilters(value: ReportFilterValue): void { this.navigate({ ...value, cursor: null }); }
  setStatus(status: string): void { this.navigate({ status: status || null, cursor: null }); }
  setCommand(command: string): void { this.navigate({ command: command || null, cursor: null }); }
  reset(): void { this.router.navigate([], { relativeTo: this.route }); }
  retry(): void { this.reloadTick.update(value => value + 1); }

  loadMore(): void {
    const guildId = this.appStore.selectedGuild()?.id;
    const current = this.report();
    if (!guildId || !current?.recent.nextCursor || this.loadingMore()) return;
    const generation = this.requestGeneration;
    this.loadMoreError.set(null);
    this.loadingMore.set(true);
    this.reporting.commands(guildId, { ...this.query(), cursor: current.recent.nextCursor }).pipe(
      finalize(() => this.loadingMore.set(false)), takeUntilDestroyed(this.destroyRef)
    ).subscribe({ next: page => {
      if (guildId !== this.appStore.selectedGuild()?.id || generation !== this.requestGeneration) return;
      this.report.set({ ...page, recent: { ...page.recent, items: [...current.recent.items, ...page.recent.items] } });
    }, error: () => { if (guildId === this.appStore.selectedGuild()?.id && generation === this.requestGeneration) this.loadMoreError.set('Weitere Aufrufe konnten nicht geladen werden.'); } });
  }

  percent(value: number): string { return new Intl.NumberFormat('de-DE', { style: 'percent', maximumFractionDigits: 1 }).format(value > 1 ? value / 100 : value); }
  number(value: number): string { return new Intl.NumberFormat('de-DE').format(value); }
  duration(value: number): string { return `${new Intl.NumberFormat('de-DE', { maximumFractionDigits: 0 }).format(value)} ms`; }
  formatDate(value: string): string { return new Intl.DateTimeFormat('de-DE', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(value)); }

  private navigate(queryParams: Record<string, string | null>): void { this.router.navigate([], { relativeTo: this.route, queryParams, queryParamsHandling: 'merge' }); }
  private readQuery(params: ParamMap): CommandQuery { return { from: params.get('from') || undefined, to: params.get('to') || undefined, search: params.get('search') || undefined, command: params.get('command') || undefined, status: (params.get('status') as CommandQuery['status']) || undefined, take: 25 }; }
}
