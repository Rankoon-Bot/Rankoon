import { CommonModule } from '@angular/common';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { finalize } from 'rxjs';
import { LocaleService } from '../../i18n/locale.service';
import { ApiErrorService } from '../../services/api-error.service';
import { AnalyticsChartPoint, AnalyticsChartSeries, AnalyticsUnit } from '../../shared/analytics-chart/analytics-chart.models';
import { AnalyticsFormatterService } from '../../shared/analytics-chart/analytics-formatter.service';
import { AnalyticsLineChartComponent } from '../../shared/analytics-chart/analytics-line-chart.component';
import { AppStore } from '../../store/app.store';
import { AnalyticsBucketSize, AnalyticsTimelineAggregate, AnalyticsTimelineBucket, AnalyticsTimelineRange, GuildAnalyticsTimeline } from './guild-analytics.models';
import { GuildAnalyticsService } from './guild-analytics.service';

type AnalyticsMetric = 'totalXp' | 'voiceXp' | 'messageXp' | 'reactionXp' | 'qualifiedVoice' | 'activeVoiceMembers' | 'voiceActivities' | 'activities' | 'messageActivities' | 'reactionActivities' | 'levelUps' | 'activeMembers';
type AnalyticsSource = 'all' | 'voice' | 'messages' | 'reactions' | 'other';
interface MetricDefinition { key: AnalyticsMetric; unit: AnalyticsUnit; value: (row: AnalyticsTimelineAggregate) => number; }

const METRICS: readonly MetricDefinition[] = [
  { key: 'totalXp', unit: 'xp', value: row => Number(row.awardedXp.total) },
  { key: 'voiceXp', unit: 'xp', value: row => Number(row.awardedXp.voice) },
  { key: 'messageXp', unit: 'xp', value: row => Number(row.awardedXp.messages) },
  { key: 'reactionXp', unit: 'xp', value: row => Number(row.awardedXp.reactions) },
  { key: 'qualifiedVoice', unit: 'duration', value: row => Number(row.qualifiedVoiceSeconds) },
  { key: 'activeVoiceMembers', unit: 'members', value: row => Number(row.activeVoiceMembers) },
  { key: 'voiceActivities', unit: 'count', value: row => Number(row.activities.voice) },
  { key: 'activities', unit: 'count', value: row => Number(row.activities.total) },
  { key: 'messageActivities', unit: 'count', value: row => Number(row.activities.messages) },
  { key: 'reactionActivities', unit: 'count', value: row => Number(row.activities.reactions) },
  { key: 'levelUps', unit: 'count', value: row => Number(row.levelUps) },
  { key: 'activeMembers', unit: 'members', value: row => Number(row.activeMembers) }
];

@Component({ selector: 'app-analytics-page', standalone: true, imports: [CommonModule, FormsModule, TranslocoPipe, AnalyticsLineChartComponent], templateUrl: './analytics-page.component.html', styleUrl: './analytics-page.component.scss' })
export class AnalyticsPageComponent {
  private readonly api = inject(GuildAnalyticsService);
  private readonly store = inject(AppStore);
  private readonly apiErrors = inject(ApiErrorService);
  private readonly i18n = inject(TranslocoService);
  readonly locale = inject(LocaleService);
  readonly formatter = inject(AnalyticsFormatterService);
  readonly page = input.required<string>();
  readonly range = signal<AnalyticsTimelineRange>('7d');
  readonly bucket = signal<AnalyticsBucketSize>('auto');
  readonly metric = signal<AnalyticsMetric>('totalXp');
  readonly source = signal<AnalyticsSource>('all');
  readonly xpView = signal<'total' | 'sources'>('total');
  readonly compare = signal(false);
  readonly filtersOpen = signal(false);
  readonly customFrom = signal('');
  readonly customTo = signal('');
  readonly data = signal<GuildAnalyticsTimeline | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly metrics = METRICS;
  readonly ranges: readonly AnalyticsTimelineRange[] = ['7d', '30d', '90d', 'custom'];
  readonly buckets: readonly AnalyticsBucketSize[] = ['auto', 'day', 'week'];
  readonly sources: readonly AnalyticsSource[] = ['all', 'voice', 'messages', 'reactions', 'other'];
  private requestId = 0;
  private guildId: string | null = null;

  readonly selectedMetric = computed(() => METRICS.find(item => item.key === this.metric()) ?? METRICS[0]);
  readonly chartSeries = computed<AnalyticsChartSeries[]>(() => {
    this.locale.locale();
    if (this.metric() === 'totalXp' && this.xpView() === 'sources') return ['voiceXp', 'messageXp', 'reactionXp', 'otherXp'].map((key, index) => ({ key, label: this.i18n.translate(`analytics.metrics.${key}`), className: `series-${index}` }));
    const series: AnalyticsChartSeries[] = [{ key: this.metric(), label: this.metricLabel(this.metric()) }];
    if (this.compare()) series.push({ key: `${this.metric()}Previous`, label: this.i18n.translate('analytics.previousPeriod'), className: 'series-1' });
    return series;
  });
  readonly chartPoints = computed<AnalyticsChartPoint[]>(() => {
    const report = this.data();
    if (!report) return [];
    return report.buckets.map((bucket, index) => {
      const values: Record<string, number> = {};
      if (this.metric() === 'totalXp' && this.xpView() === 'sources') {
        values['voiceXp'] = Number(bucket.awardedXp.voice); values['messageXp'] = Number(bucket.awardedXp.messages); values['reactionXp'] = Number(bucket.awardedXp.reactions); values['otherXp'] = Number(bucket.awardedXp.other);
      } else {
        values[this.metric()] = this.filteredValue(bucket);
        values[`${this.metric()}Previous`] = report.previousBuckets[index] ? this.filteredValue(report.previousBuckets[index]) : 0;
      }
      return { timestamp: bucket.start, end: bucket.end, isIncomplete: bucket.isIncomplete, values };
    });
  });
  readonly summary = computed(() => {
    const report = this.data(); const definition = this.selectedMetric();
    if (!report) return null;
    const total = this.filteredValue(report.summary.current);
    const previous = this.filteredValue(report.summary.previous);
    const completeBuckets = report.buckets.filter(item => !item.isIncomplete);
    const strongestRows = completeBuckets.length ? completeBuckets : report.buckets;
    const strongest = strongestRows.reduce<AnalyticsTimelineBucket | null>((best, item) => !best || this.filteredValue(item) > this.filteredValue(best) ? item : best, null);
    const averageRows = completeBuckets.length ? completeBuckets : report.buckets;
    const average = averageRows.reduce((sum, item) => sum + this.filteredValue(item), 0) / Math.max(averageRows.length, 1);
    return { total, average, strongest, change: previous === 0 ? null : (total - previous) * 100 / Math.abs(previous), unit: definition.unit, incomplete: report.buckets.some(item => item.isIncomplete) };
  });

  constructor() {
    effect(() => {
      const page = this.page();
      this.metric.set(page === 'voice' ? 'qualifiedVoice' : page === 'features' ? 'activities' : page === 'overview' ? 'activeMembers' : 'totalXp');
    }, { allowSignalWrites: true });
    effect(() => {
      const guild = this.store.selectedGuild()?.id ?? null; this.range(); this.bucket();
      if (guild !== this.guildId) { this.guildId = guild; this.requestId++; this.data.set(null); this.error.set(null); }
      if (guild && this.range() !== 'custom') this.load(guild);
      else if (!guild) this.loading.set(false);
    }, { allowSignalWrites: true });
  }

  setRange(value: AnalyticsTimelineRange): void { this.range.set(value); }
  setBucket(value: AnalyticsBucketSize): void { this.bucket.set(value); }
  setMetric(value: string): void { this.metric.set(value as AnalyticsMetric); if (value !== 'totalXp' && value !== 'activities') this.source.set('all'); }
  setSource(value: string): void { this.source.set(value as AnalyticsSource); }
  refresh(): void { const guild = this.store.selectedGuild()?.id; if (guild) this.load(guild); }
  applyCustom(): void { if (this.customFrom() && this.customTo()) this.refresh(); }
  metricLabel(metric: AnalyticsMetric): string { return this.i18n.translate(`analytics.metrics.${metric}`); }
  value(value: number): string { return this.formatter.value(value, this.selectedMetric().unit); }
  strongestLabel(bucket: AnalyticsTimelineBucket | null): string { return bucket ? this.formatter.date(bucket.start, true, this.data()?.timeZone) : '-'; }

  filteredValue(row: AnalyticsTimelineAggregate): number {
    const source = this.source();
    if (this.metric() === 'totalXp' && source !== 'all') return Number(row.awardedXp[source]);
    if (this.metric() === 'activities' && source !== 'all') return Number(row.activities[source]);
    return this.selectedMetric().value(row);
  }

  private load(guildId: string): void {
    if (this.range() === 'custom' && (!this.customFrom() || !this.customTo())) return;
    const request = ++this.requestId;
    this.loading.set(true); this.error.set(null);
    this.api.timeline(guildId, { range: this.range(), bucket: this.bucket(), from: this.range() === 'custom' ? new Date(this.customFrom()).toISOString() : undefined, to: this.range() === 'custom' ? new Date(this.customTo()).toISOString() : undefined }).pipe(finalize(() => { if (request === this.requestId) this.loading.set(false); })).subscribe({
      next: data => { if (request === this.requestId) this.data.set(data); },
      error: error => { if (request === this.requestId) this.error.set(this.apiErrors.resolve(error, 'errors.analyticsLoad').message); }
    });
  }
}
