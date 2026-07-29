import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { environment } from '../../../environments/environment';
import { AnalyticsPage, AnalyticsRange, AnalyticsTimelineQuery, GuildAnalyticsAudit, GuildAnalyticsFeatures, GuildAnalyticsOverview, GuildAnalyticsTimeline, GuildAnalyticsVoice, GuildAnalyticsXp } from './guild-analytics.models';

@Injectable({ providedIn: 'root' })
export class GuildAnalyticsService {
  private readonly http = inject(HttpClient);

  overview(guildId: string, range: AnalyticsRange) { return this.get<GuildAnalyticsOverview>(guildId, 'overview', range); }
  xp(guildId: string, range: AnalyticsRange) { return this.get<GuildAnalyticsXp>(guildId, 'xp', range); }
  voice(guildId: string, range: AnalyticsRange) { return this.get<GuildAnalyticsVoice>(guildId, 'voice', range); }
  features(guildId: string, range: AnalyticsRange) { return this.get<GuildAnalyticsFeatures>(guildId, 'features', range); }
  timeline(guildId: string, query: AnalyticsTimelineQuery) {
    let params = new HttpParams().set('range', query.range).set('bucket', query.bucket);
    if (query.from) params = params.set('from', query.from);
    if (query.to) params = params.set('to', query.to);
    return this.http.get<GuildAnalyticsTimeline>(this.url(guildId, 'timeline'), { params });
  }
  audit(guildId: string, range: AnalyticsRange, cursor?: string) {
    let params = new HttpParams().set('range', range);
    if (cursor) params = params.set('cursor', cursor);
    return this.http.get<GuildAnalyticsAudit>(this.url(guildId, 'audit'), { params });
  }

  private get<T>(guildId: string, page: AnalyticsPage, range: AnalyticsRange) {
    return this.http.get<T>(this.url(guildId, page), { params: { range } });
  }

  private url(guildId: string, page: AnalyticsPage): string {
    return `${environment.apiBaseUrl}/guilds/${encodeURIComponent(guildId)}/analytics/${page}`;
  }
}
