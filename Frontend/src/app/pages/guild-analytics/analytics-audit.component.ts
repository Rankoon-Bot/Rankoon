import { CommonModule } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';
import { catchError, EMPTY, finalize } from 'rxjs';
import { LocaleService } from '../../i18n/locale.service';
import { ApiErrorService } from '../../services/api-error.service';
import { DomainValueService } from '../../i18n/domain-value.service';
import { AnalyticsPageBase } from './analytics-page.base';
import { AnalyticsAuditItem, AnalyticsRange, GuildAnalyticsAudit } from './guild-analytics.models';
import { GuildAnalyticsService } from './guild-analytics.service';

@Component({ selector: 'app-analytics-audit', standalone: true, imports: [CommonModule, TranslocoPipe], template: `
  <header class="rk-page-header"><div><p class="rk-page-header__eyebrow">{{ 'analytics.eyebrow' | transloco }}</p><h1>{{ 'analytics.pages.audit.title' | transloco }}</h1><p class="rk-page-header__description">{{ 'analytics.pages.audit.description' | transloco }}</p></div><div class="range" role="group" [attr.aria-label]="'analytics.range' | transloco">@for (option of ranges; track option) { <button type="button" [attr.aria-pressed]="range() === option" [class.active]="range() === option" (click)="changeRange(option)">{{ ('analytics.ranges.' + option) | transloco }}</button> }</div></header>
  @if (loading() && !data()) { <section class="rk-panel state" role="status">{{ 'analytics.loading' | transloco }}</section> }
  @if (error()) { <section class="rk-notice rk-notice--danger" role="alert">{{ error() }} <button class="rk-button" type="button" (click)="load()">{{ 'common.retry' | transloco }}</button></section> }
  @if (data(); as report) {
    <section class="rk-panel audit">
      <h2>{{ 'analytics.audit.title' | transloco }}</h2><p>{{ 'analytics.audit.description' | transloco }}</p>
       <ol>@for (item of report.items; track item.id) { <li><time>{{ locale.date(item.occurredAt, { dateStyle: 'medium', timeStyle: 'short' }) }}</time><div class="event"><strong>{{ eventLabel(item) }}</strong><span>{{ outcomeLabel(item.outcome) }}</span><div class="references">@if (item.actorName || item.actorId) { <span><b>{{ 'activity.actor' | transloco }}:</b> {{ item.actorName || item.actorId }}</span> } @if (item.subjectName || item.subjectId) { <span><b>{{ 'analytics.audit.subject' | transloco }}:</b> {{ item.subjectName || item.subjectId }}</span> } @if (item.channelName || item.channelId) { <span><b>{{ 'activity.channel' | transloco }}:</b> {{ item.channelName || item.channelId }}</span> } @if (item.hubName || item.metadata['hubId']) { <span><b>{{ 'analytics.audit.hub' | transloco }}:</b> {{ item.hubName || item.metadata['hubId'] }}</span> }</div></div><div class="technical">@if (item.correlationId) { <code>{{ item.correlationId }}</code> } @if (hasMetadata(item)) { <details><summary>{{ 'analytics.audit.metadata' | transloco }}</summary><dl>@for (entry of metadataEntries(item); track entry[0]) { <dt>{{ entry[0] }}</dt><dd>{{ entry[1] }}</dd> }</dl></details> }</div></li> } @empty { <li class="empty">{{ 'analytics.audit.empty' | transloco }}</li> }</ol>
      @if (loadMoreError()) { <p class="rk-notice rk-notice--danger" role="alert">{{ loadMoreError() }}</p> }
      @if (report.nextCursor) { <button class="rk-button" type="button" [disabled]="loadingMore()" (click)="loadMore()">{{ loadingMore() ? ('common.loading' | transloco) : ('common.loadMore' | transloco) }}</button> }
    </section>
  }
`, styles: [`
  .range { display: flex; flex-wrap: wrap; gap: var(--rk-space-1); }.range button { min-height: var(--rk-control-height); padding: 0 var(--rk-space-3); color: var(--rk-text-muted); background: var(--rk-surface-2); border: 1px solid var(--rk-border-subtle); border-radius: var(--rk-radius-sm); }.range button.active { color: var(--rk-text-strong); border-color: var(--rk-info); }.state { margin-top: var(--rk-space-4); }.audit { margin-top: var(--rk-space-4); }.audit > p, time, span, .empty { color: var(--rk-text-muted); }.audit ol { display: grid; gap: var(--rk-space-2); margin: var(--rk-space-4) 0; padding: 0; list-style: none; }.audit li { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; gap: var(--rk-space-4); align-items: start; padding: var(--rk-space-3); background: var(--rk-surface-2); border-radius: var(--rk-radius-md); }.event, .references, .technical { display: grid; gap: var(--rk-space-1); }.references { grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr)); margin-top: var(--rk-space-2); }.references b { color: var(--rk-text); font-weight: 600; }.technical { justify-items: end; }.audit code { color: var(--rk-info); overflow-wrap: anywhere; }.technical details { width: 100%; max-width: 24rem; }.technical summary { color: var(--rk-info); cursor: pointer; }.technical dl { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: var(--rk-space-1) var(--rk-space-2); margin: var(--rk-space-2) 0 0; padding: var(--rk-space-2); border: 1px solid var(--rk-border-subtle); border-radius: var(--rk-radius-sm); }.technical dt { color: var(--rk-text-muted); }.technical dd { margin: 0; overflow-wrap: anywhere; }
  @media (max-width: 768px) { .audit li { grid-template-columns: 1fr; gap: var(--rk-space-2); } }
`] })
export class AnalyticsAuditComponent extends AnalyticsPageBase<GuildAnalyticsAudit> {
  private readonly api = inject(GuildAnalyticsService);
  private readonly auditErrors = inject(ApiErrorService);
  private readonly domain = inject(DomainValueService);
  private readonly auditDestroyRef = inject(DestroyRef);
  readonly locale = inject(LocaleService);
  readonly loadingMore = signal(false);
  readonly loadMoreError = signal<string | null>(null);
  readonly ranges: readonly AnalyticsRange[] = ['24h', '7d', '30d', '90d'];
  constructor() { super(); this.initialize(); }
  protected request(id: string, range: AnalyticsRange) { return this.api.audit(id, range); }
  eventLabel(item: AnalyticsAuditItem): string { return this.domain.activityName(item.code); }
  outcomeLabel(value: string): string { return this.domain.outcome(value); }
  hasMetadata(item: AnalyticsAuditItem): boolean { return Object.keys(item.metadata).length > 0; }
  metadataEntries(item: AnalyticsAuditItem): [string, string][] { return Object.entries(item.metadata).map(([key, value]) => [key, value == null ? '—' : String(value)]); }
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
