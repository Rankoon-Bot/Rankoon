import { DestroyRef, Directive, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { catchError, EMPTY, finalize, Observable } from 'rxjs';
import { ApiErrorService } from '../../services/api-error.service';
import { AppStore } from '../../store/app.store';
import { AnalyticsRange, GuildAnalyticsResponse } from './guild-analytics.models';

@Directive()
export abstract class AnalyticsPageBase<T extends GuildAnalyticsResponse> {
  protected readonly appStore = inject(AppStore);
  protected readonly route = inject(ActivatedRoute);
  protected readonly router = inject(Router);
  private readonly errors = inject(ApiErrorService);
  private readonly destroyRef = inject(DestroyRef);
  readonly range = signal<AnalyticsRange>('7d');
  readonly data = signal<T | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  protected initialize(): void {
    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(params => {
      const range = params.get('range');
      this.range.set(range === '24h' || range === '30d' || range === '90d' ? range : '7d');
      this.load();
    });
  }

  changeRange(range: AnalyticsRange): void {
    if (range !== this.range()) void this.router.navigate([], { relativeTo: this.route, queryParams: { range } });
  }

  load(): void {
    const guildId = this.appStore.selectedGuild()?.id;
    if (!guildId) { this.loading.set(false); return; }
    this.loading.set(true); this.error.set(null);
    this.request(guildId, this.range()).pipe(
      catchError(error => { this.error.set(this.errors.resolve(error, 'errors.analyticsLoad').message); return EMPTY; }),
      finalize(() => this.loading.set(false)), takeUntilDestroyed(this.destroyRef)
    ).subscribe(data => this.data.set(data));
  }

  protected abstract request(guildId: string, range: AnalyticsRange): Observable<T>;
}
