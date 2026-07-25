import { CommonModule } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';
import { catchError, EMPTY, finalize } from 'rxjs';
import { LocaleService } from '../../i18n/locale.service';
import { ApiErrorService } from '../../services/api-error.service';
import { AnalyticsPageComponent } from './analytics-page.component';
import { AnalyticsPageBase } from './analytics-page.base';
import { AnalyticsAuditItem, AnalyticsRange, GuildAnalyticsAudit } from './guild-analytics.models';
import { GuildAnalyticsService } from './guild-analytics.service';

@Component({ selector: 'app-analytics-audit', standalone: true, imports: [CommonModule, TranslocoPipe, AnalyticsPageComponent], template: `
  <app-analytics-page page="audit" [range]="range()" [data]="data()" [loading]="loading()" [error]="error()" (rangeChange)="changeRange($event)" (retry)="load()" />
  @if (data(); as report) {
    <section class="rk-panel audit">
      <h2>{{ 'analytics.audit.title' | transloco }}</h2><p>{{ 'analytics.audit.description' | transloco }}</p>
      <ol>@for (item of report.items; track item.id) { <li><time>{{ locale.date(item.occurredAt, { dateStyle: 'medium', timeStyle: 'short' }) }}</time><div><strong>{{ eventLabel(item) }}</strong><span>{{ item.actorName || item.actorId || ('common.system' | transloco) }} &middot; {{ item.outcome }}</span></div>@if (item.correlationId) { <code>{{ item.correlationId }}</code> }</li> } @empty { <li class="empty">{{ 'analytics.audit.empty' | transloco }}</li> }</ol>
      @if (loadMoreError()) { <p class="rk-notice rk-notice--danger" role="alert">{{ loadMoreError() }}</p> }
      @if (report.nextCursor) { <button class="rk-button" type="button" [disabled]="loadingMore()" (click)="loadMore()">{{ loadingMore() ? ('common.loading' | transloco) : ('common.loadMore' | transloco) }}</button> }
    </section>
  }
`, styles: [`
  .audit { margin-top: var(--rk-space-4); }.audit > p, time, span, .empty { color: var(--rk-text-muted); }.audit ol { display: grid; gap: var(--rk-space-2); margin: var(--rk-space-4) 0; padding: 0; list-style: none; }.audit li { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; gap: var(--rk-space-4); align-items: center; padding: var(--rk-space-3); background: var(--rk-surface-2); border-radius: var(--rk-radius-md); }.audit li div, .audit li span { display: grid; }.audit code { color: var(--rk-info); overflow-wrap: anywhere; }
  @media (max-width: 768px) { .audit li { grid-template-columns: 1fr; gap: var(--rk-space-2); } }
`] })
export class AnalyticsAuditComponent extends AnalyticsPageBase<GuildAnalyticsAudit> {
  private readonly api = inject(GuildAnalyticsService);
  private readonly auditErrors = inject(ApiErrorService);
  private readonly auditDestroyRef = inject(DestroyRef);
  readonly locale = inject(LocaleService);
  readonly loadingMore = signal(false);
  readonly loadMoreError = signal<string | null>(null);
  constructor() { super(); this.initialize(); }
  protected request(id: string, range: AnalyticsRange) { return this.api.audit(id, range); }
  eventLabel(item: AnalyticsAuditItem): string { return item.code; }
  loadMore(): void {
    const current = this.data(); const guildId = this.appStore.selectedGuild()?.id;
    if (!current?.nextCursor || !guildId || this.loadingMore()) return;
    this.loadingMore.set(true); this.loadMoreError.set(null);
    this.api.audit(guildId, this.range(), current.nextCursor).pipe(
      catchError(error => { this.loadMoreError.set(this.auditErrors.resolve(error, 'errors.analyticsLoad').message); return EMPTY; }),
      finalize(() => this.loadingMore.set(false)), takeUntilDestroyed(this.auditDestroyRef)
    ).subscribe(page => this.data.set({ ...page, items: [...current.items, ...page.items] }));
  }
}
