export type ReportStatus = 'success' | 'warning' | 'error' | 'info' | 'neutral';

export interface DateRangeQuery {
  from?: string;
  to?: string;
  search?: string;
  cursor?: string;
  take?: number;
}

export interface CursorPage<T> {
  items: T[];
  nextCursor: string | null;
  hasMore: boolean;
}

export interface ReportItemDto {
  id: string;
  category: 'activity' | 'command' | 'error';
  name: string;
  action: string | null;
  outcome: 'succeeded' | 'failed' | 'rejected' | string;
  severity: 'info' | 'warning' | 'error' | 'critical' | null;
  actorId: string | null;
  subjectId: string | null;
  channelId: string | null;
  correlationId: string | null;
  durationMs: string | number | null;
  metadata: Record<string, string>;
  occurredAt: string;
}

export interface ReportListDto {
  items: ReportItemDto[];
  nextCursor: string | null;
}

export interface ReportSummaryGroupDto { key: string; count: string | number; }
export interface ReportMetricGroupDto { key: string; count: string | number; succeeded: string | number; failed: string | number; averageDurationMs: string | number | null; firstSeenAt: string; lastSeenAt: string; }
export interface ReportTrendDto { timestamp: string; total: string | number; succeeded: string | number; failed: string | number; }
export interface ReportSummaryDto {
  total: string | number;
  succeeded: string | number;
  failed: string | number;
  averageDurationMs: string | number | null;
  uniqueActors: string | number;
  uniqueCommands: string | number;
  byName: ReportSummaryGroupDto[];
  byOutcome: ReportSummaryGroupDto[];
  groups: ReportMetricGroupDto[];
  trend: ReportTrendDto[];
}

export interface ActivityQuery extends DateRangeQuery {
  type?: string;
  actorId?: string;
}

export interface ActivityEventDto {
  id: string;
  occurredAt: string;
  type: string;
  title: string;
  description: string | null;
  actorId: string | null;
  actorName: string | null;
  channelId: string | null;
  channelName: string | null;
  status: ReportStatus;
  metadata: Record<string, string | number | boolean | null> | null;
  correlationId: string | null;
}

export interface ActivityReportDto extends CursorPage<ActivityEventDto> {
  availableTypes: string[];
}

export interface CommandQuery extends DateRangeQuery {
  command?: string;
  status?: 'success' | 'failed';
}

export interface TrendPointDto {
  timestamp: string;
  value: number;
  secondaryValue?: number;
}

export interface CommandSummaryDto {
  totalInvocations: number;
  successRate: number;
  uniqueUsers: number;
  averageDurationMs: number;
}

export interface CommandBreakdownDto {
  command: string;
  invocations: number;
  successRate: number;
  averageDurationMs: number;
}

export interface CommandInvocationDto {
  id: string;
  occurredAt: string;
  command: string;
  userId: string;
  userName: string;
  channelName: string | null;
  durationMs: number;
  succeeded: boolean;
  correlationId: string | null;
}

export interface CommandReportDto {
  summary: CommandSummaryDto;
  trend: TrendPointDto[];
  byCommand: CommandBreakdownDto[];
  recent: CursorPage<CommandInvocationDto>;
}

export interface ErrorQuery extends DateRangeQuery {
  severity?: 'warning' | 'error' | 'critical';
  source?: string;
  correlationId?: string;
}

export interface ErrorSummaryDto {
  totalErrors: number;
  affectedUsers: number;
  affectedCommands: number;
  unresolvedErrors: number;
}

export interface ErrorGroupDto {
  fingerprint: string;
  title: string;
  source: string;
  severity: 'warning' | 'error' | 'critical';
  count: number;
  firstSeenAt: string;
  lastSeenAt: string;
}

export interface ErrorEventDto {
  id: string;
  occurredAt: string;
  title: string;
  message: string;
  source: string;
  severity: 'warning' | 'error' | 'critical';
  command: string | null;
  userId: string | null;
  userName: string | null;
  correlationId: string | null;
  stackTrace: string | null;
  metadata: Record<string, string | number | boolean | null> | null;
}

export interface ErrorReportDto {
  summary: ErrorSummaryDto;
  groups: ErrorGroupDto[];
  events: CursorPage<ErrorEventDto>;
  availableSources: string[];
}
