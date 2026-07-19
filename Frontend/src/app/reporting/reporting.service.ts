import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { forkJoin, map } from 'rxjs';
import { environment } from '../../environments/environment';
import { ActivityEventDto, ActivityQuery, ActivityReportDto, CommandInvocationDto, CommandQuery, CommandReportDto, DateRangeQuery, ErrorEventDto, ErrorQuery, ErrorReportDto, ReportItemDto, ReportListDto, ReportStatus, ReportSummaryDto } from './reporting.models';

@Injectable({ providedIn: 'root' })
export class ReportingService {
  private readonly http = inject(HttpClient);

  activity(guildId: string, query: ActivityQuery): Observable<ActivityReportDto> {
    return this.request(guildId, 'activity', query, { name: query.type, actorId: query.actorId }).pipe(map(({ list, summary }) => {
      const items = list.items.map(item => this.activityItem(item));
      return { items, nextCursor: list.nextCursor, hasMore: list.nextCursor !== null, availableTypes: summary.byName.map(group => group.key) };
    }));
  }

  commands(guildId: string, query: CommandQuery): Observable<CommandReportDto> {
    const outcome = query.status === 'success' ? 'succeeded' : query.status === 'failed' ? 'failed' : undefined;
    return this.request(guildId, 'commands', query, { name: query.command, outcome }).pipe(map(({ list, summary }) => {
       const rawItems = list.items;
       const recent = rawItems.map(item => this.commandItem(item));
       const total = this.number(summary.total);
       const successRate = total ? this.number(summary.succeeded) / total : 0;
       return {
         summary: { totalInvocations: total, successRate, uniqueUsers: this.number(summary.uniqueActors), averageDurationMs: this.number(summary.averageDurationMs) },
         trend: summary.trend.map(point => ({ timestamp: point.timestamp, value: this.number(point.total), secondaryValue: this.number(point.failed) })),
         byCommand: summary.groups.map(group => ({ command: group.key, invocations: this.number(group.count), successRate: this.number(group.count) ? this.number(group.succeeded) / this.number(group.count) : 0, averageDurationMs: this.number(group.averageDurationMs) })),
        recent: { items: recent, nextCursor: list.nextCursor, hasMore: list.nextCursor !== null }
      };
    }));
  }

  errors(guildId: string, query: ErrorQuery): Observable<ErrorReportDto> {
    return this.request(guildId, 'errors', query, { name: query.source, severity: query.severity, correlationId: query.correlationId }).pipe(map(({ list, summary }) => {
      const rawItems = list.items;
      const events = rawItems.map(item => this.errorItem(item));
      return {
        summary: {
          totalErrors: this.number(summary.total),
          affectedUsers: this.number(summary.uniqueActors),
          affectedCommands: this.number(summary.uniqueCommands),
          unresolvedErrors: this.number(summary.failed)
        },
        groups: summary.groups.map(group => ({ fingerprint: group.key, title: this.humanize(group.key), source: group.key, severity: 'error' as const, count: this.number(group.count), firstSeenAt: group.firstSeenAt, lastSeenAt: group.lastSeenAt })),
        events: { items: events, nextCursor: list.nextCursor, hasMore: list.nextCursor !== null },
        availableSources: summary.byName.map(group => group.key)
      };
    }));
  }

  private request(guildId: string, report: string, query: DateRangeQuery, filters: { name?: string; action?: string; outcome?: string; severity?: string; actorId?: string; correlationId?: string }): Observable<{ list: ReportListDto; summary: ReportSummaryDto }> {
    const params = this.params({ from: query.from, to: query.to, search: query.search, cursor: query.cursor, take: query.take, ...filters });
    const summaryParams = this.params({ from: query.from, to: query.to, search: query.search, ...filters });
    const url = this.url(guildId, report);
    return forkJoin({ list: this.http.get<ReportListDto>(url, { params }), summary: this.http.get<ReportSummaryDto>(`${url}/summary`, { params: summaryParams }) });
  }

  private url(guildId: string, report: string): string {
    return `${environment.apiBaseUrl}/guilds/${encodeURIComponent(guildId)}/reports/${report}`;
  }

  private params(query: object): HttpParams {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(query)) {
      if (value !== undefined && value !== null && value !== '') {
        const serialized = (key === 'from' || key === 'to') ? this.utc(String(value)) : String(value);
        params = params.set(key, serialized);
      }
    }
    return params;
  }

  private activityItem(item: ReportItemDto): ActivityEventDto {
    return { id: item.id, occurredAt: item.occurredAt, type: item.name, title: this.humanize(item.name), description: item.action ? this.humanize(item.action) : null, actorId: item.actorId, actorName: item.actorId, channelId: item.channelId ?? item.metadata['channelId'] ?? item.metadata['voiceChannelId'] ?? null, channelName: null, status: this.status(item.outcome), metadata: item.metadata, correlationId: item.correlationId };
  }

  private commandItem(item: ReportItemDto): CommandInvocationDto {
    return { id: item.id, occurredAt: item.occurredAt, command: item.name, userId: item.actorId ?? '', userName: item.actorId ?? 'System', channelName: item.channelId, durationMs: this.number(item.durationMs), succeeded: item.outcome === 'succeeded', correlationId: item.correlationId };
  }

  private errorItem(item: ReportItemDto): ErrorEventDto {
    const source = item.action ?? item.name;
    return { id: item.id, occurredAt: item.occurredAt, title: item.metadata['errorType'] ?? this.humanize(item.name), message: `Fehler in ${this.humanize(source)}`, source, severity: this.errorSeverity(item), command: item.metadata['command'] ?? null, userId: item.actorId, userName: item.actorId, correlationId: item.correlationId, stackTrace: null, metadata: item.metadata };
  }

  private status(outcome: string): ReportStatus { return outcome === 'succeeded' ? 'success' : outcome === 'rejected' ? 'warning' : outcome === 'failed' ? 'error' : 'info'; }
  private errorSeverity(item: ReportItemDto): ErrorEventDto['severity'] { return item.severity === 'critical' ? 'critical' : item.severity === 'warning' ? 'warning' : 'error'; }
  private humanize(value: string): string { return value.split(/[._-]/).filter(Boolean).map(part => part.charAt(0).toLocaleUpperCase('de-DE') + part.slice(1)).join(' '); }
  private number(value: string | number | null | undefined): number { const result = Number(value ?? 0); return Number.isFinite(result) ? result : 0; }
  private utc(value: string): string { const date = new Date(value); return Number.isNaN(date.getTime()) ? value : date.toISOString(); }
}
