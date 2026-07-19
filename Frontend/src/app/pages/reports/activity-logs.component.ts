import { CommonModule } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { catchError, combineLatest, EMPTY, finalize, switchMap, tap } from 'rxjs';
import { ReportFilterBarComponent, ReportFilterValue } from '../../reporting/components/report-filter-bar.component';
import { ReportStateComponent } from '../../reporting/components/report-state.component';
import { ReportStatusComponent } from '../../reporting/components/report-status.component';
import { ActivityEventDto, ActivityQuery, ActivityReportDto } from '../../reporting/reporting.models';
import { ReportingService } from '../../reporting/reporting.service';
import { AppStore } from '../../store/app.store';
import { TranslocoPipe } from '@jsverse/transloco';
import { LocaleService } from '../../i18n/locale.service';
import { ApiErrorService } from '../../services/api-error.service';
import { DomainValueService } from '../../i18n/domain-value.service';

@Component({
  selector: 'app-activity-logs', standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive, ReportFilterBarComponent, ReportStateComponent, ReportStatusComponent, TranslocoPipe],
  templateUrl: './activity-logs.component.html', styleUrl: './activity-logs.component.scss'
})
export class ActivityLogsComponent {
  private readonly appStore = inject(AppStore);
  private readonly reporting = inject(ReportingService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly locale = inject(LocaleService);
  private readonly apiErrors = inject(ApiErrorService);
  private readonly domain = inject(DomainValueService);
  private readonly reloadTick = signal(0);
  private requestGeneration = 0;
  readonly report = signal<ActivityReportDto | null>(null);
  readonly loading = signal(true);
  readonly loadingMore = signal(false);
  readonly loadMoreError = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly selected = signal<ActivityEventDto | null>(null);
  readonly query = signal<ActivityQuery>({ take: 30 });

  constructor() {
    combineLatest([toObservable(this.appStore.selectedGuild), this.route.queryParamMap, toObservable(this.reloadTick)]).pipe(
      tap(([, params]) => { this.requestGeneration++; this.query.set(this.readQuery(params)); this.report.set(null); this.loading.set(true); this.error.set(null); this.loadMoreError.set(null); this.selected.set(null); }),
      switchMap(([guild]) => {
        const generation = this.requestGeneration;
        if (!guild) { this.loading.set(false); return EMPTY; }
        return this.reporting.activity(guild.id, this.query()).pipe(
           catchError(error => { if (generation === this.requestGeneration) this.error.set(this.apiErrors.resolve(error, 'errors.activityLoad').message); return EMPTY; }),
          finalize(() => { if (generation === this.requestGeneration) this.loading.set(false); })
        );
      }),
      takeUntilDestroyed(this.destroyRef)
    ).subscribe(report => this.report.set(report));
  }

  applyFilters(value: ReportFilterValue): void { this.navigate({ ...value, cursor: null }); }
  setType(type: string): void { this.navigate({ type: type || null, cursor: null }); }
  reset(): void { this.router.navigate([], { relativeTo: this.route }); }
  retry(): void { this.reloadTick.update(value => value + 1); }

  loadMore(): void {
    const guildId = this.appStore.selectedGuild()?.id;
    const current = this.report();
    if (!guildId || !current?.nextCursor || this.loadingMore()) return;
    const generation = this.requestGeneration;
    this.loadMoreError.set(null);
    this.loadingMore.set(true);
    this.reporting.activity(guildId, { ...this.query(), cursor: current.nextCursor }).pipe(
      finalize(() => this.loadingMore.set(false)), takeUntilDestroyed(this.destroyRef)
    ).subscribe({ next: page => {
      if (guildId !== this.appStore.selectedGuild()?.id || generation !== this.requestGeneration) return;
      this.report.set({ ...page, items: [...current.items, ...page.items] });
     }, error: error => { if (guildId === this.appStore.selectedGuild()?.id && generation === this.requestGeneration) this.loadMoreError.set(this.apiErrors.resolve(error, 'errors.activityMore').message); } });
  }

  formatDate(value: string): string { return this.locale.date(value, { dateStyle: 'medium', timeStyle: 'short' }); }
  metadata(value: ActivityEventDto): string { return JSON.stringify(value.metadata, null, 2); }
  loadedCount(value: number): string { return this.locale.plural(value, 'activity.loadedOne', 'activity.loadedOther'); }
  typeLabel(value: string): string { return this.domain.activityName(value); }

  private navigate(queryParams: Record<string, string | null>): void { this.router.navigate([], { relativeTo: this.route, queryParams, queryParamsHandling: 'merge' }); }
  private readQuery(params: import('@angular/router').ParamMap): ActivityQuery {
    return { from: params.get('from') || undefined, to: params.get('to') || undefined, search: params.get('search') || undefined, type: params.get('type') || undefined, actorId: params.get('actorId') || undefined, take: 30 };
  }
}
