import { CommonModule } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { catchError, combineLatest, EMPTY, finalize, switchMap, tap } from 'rxjs';
import { ReportFilterBarComponent, ReportFilterValue } from '../../reporting/components/report-filter-bar.component';
import { ReportKpiComponent } from '../../reporting/components/report-kpi.component';
import { ReportStateComponent } from '../../reporting/components/report-state.component';
import { ReportStatusComponent } from '../../reporting/components/report-status.component';
import { ErrorEventDto, ErrorQuery, ErrorReportDto, ReportStatus } from '../../reporting/reporting.models';
import { ReportingService } from '../../reporting/reporting.service';
import { AppStore } from '../../store/app.store';

@Component({
  selector: 'app-error-logs', standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive, ReportFilterBarComponent, ReportKpiComponent, ReportStateComponent, ReportStatusComponent],
  templateUrl: './error-logs.component.html', styleUrl: './error-logs.component.scss'
})
export class ErrorLogsComponent {
  private readonly appStore = inject(AppStore);
  private readonly reporting = inject(ReportingService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly reloadTick = signal(0);
  private requestGeneration = 0;
  readonly report = signal<ErrorReportDto | null>(null);
  readonly query = signal<ErrorQuery>({ take: 25 });
  readonly loading = signal(true);
  readonly loadingMore = signal(false);
  readonly loadMoreError = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly selected = signal<ErrorEventDto | null>(null);

  constructor() {
    combineLatest([toObservable(this.appStore.selectedGuild), this.route.queryParamMap, toObservable(this.reloadTick)]).pipe(
      tap(([, params]) => { this.requestGeneration++; this.query.set(this.readQuery(params)); this.report.set(null); this.loading.set(true); this.error.set(null); this.loadMoreError.set(null); this.selected.set(null); }),
      switchMap(([guild]) => {
        const generation = this.requestGeneration;
        if (!guild) { this.loading.set(false); return EMPTY; }
        return this.reporting.errors(guild.id, this.query()).pipe(
          catchError(() => { if (generation === this.requestGeneration) this.error.set('Die Fehlerdaten konnten nicht geladen werden.'); return EMPTY; }),
          finalize(() => { if (generation === this.requestGeneration) this.loading.set(false); })
        );
      }), takeUntilDestroyed(this.destroyRef)
    ).subscribe(report => this.report.set(report));
  }

  applyFilters(value: ReportFilterValue): void { this.navigate({ ...value, cursor: null }); }
  setSeverity(severity: string): void { this.navigate({ severity: severity || null, cursor: null }); }
  setSource(source: string): void { this.navigate({ source: source || null, cursor: null }); }
  correlate(correlationId: string | null): void { if (correlationId) { this.selected.set(null); this.navigate({ correlationId, cursor: null }); } }
  clearCorrelation(): void { this.navigate({ correlationId: null, cursor: null }); }
  reset(): void { this.router.navigate([], { relativeTo: this.route }); }
  retry(): void { this.reloadTick.update(value => value + 1); }

  loadMore(): void {
    const guildId = this.appStore.selectedGuild()?.id;
    const current = this.report();
    if (!guildId || !current?.events.nextCursor || this.loadingMore()) return;
    const generation = this.requestGeneration;
    this.loadMoreError.set(null);
    this.loadingMore.set(true);
    this.reporting.errors(guildId, { ...this.query(), cursor: current.events.nextCursor }).pipe(
      finalize(() => this.loadingMore.set(false)), takeUntilDestroyed(this.destroyRef)
    ).subscribe({ next: page => {
      if (guildId !== this.appStore.selectedGuild()?.id || generation !== this.requestGeneration) return;
      this.report.set({ ...page, events: { ...page.events, items: [...current.events.items, ...page.events.items] } });
    }, error: () => { if (guildId === this.appStore.selectedGuild()?.id && generation === this.requestGeneration) this.loadMoreError.set('Weitere Fehler konnten nicht geladen werden.'); } });
  }

  status(severity: ErrorEventDto['severity']): ReportStatus { return severity === 'warning' ? 'warning' : 'error'; }
  number(value: number): string { return new Intl.NumberFormat('de-DE').format(value); }
  formatDate(value: string): string { return new Intl.DateTimeFormat('de-DE', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(value)); }
  metadata(value: ErrorEventDto): string { return JSON.stringify(value.metadata, null, 2); }

  private navigate(queryParams: Record<string, string | null>): void { this.router.navigate([], { relativeTo: this.route, queryParams, queryParamsHandling: 'merge' }); }
  private readQuery(params: ParamMap): ErrorQuery { return { from: params.get('from') || undefined, to: params.get('to') || undefined, search: params.get('search') || undefined, severity: (params.get('severity') as ErrorQuery['severity']) || undefined, source: params.get('source') || undefined, correlationId: params.get('correlationId') || undefined, take: 25 }; }
}
