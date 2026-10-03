export const ANALYTICS_RANGES = ['24h', '7d', '30d', '90d'] as const;
export type AnalyticsRange = typeof ANALYTICS_RANGES[number];
export type AnalyticsPage = 'overview' | 'xp' | 'voice' | 'features' | 'audit' | 'timeline';
export type AnalyticsDirection = 'up' | 'down' | 'flat';
export type AnalyticsSeverity = 'info' | 'success' | 'warning' | 'critical';

export interface AnalyticsPeriod {
  range: AnalyticsRange;
  from: string;
  to: string;
  previousFrom: string;
  previousTo: string;
}

export interface AnalyticsKpi {
  code: string;
  value: number;
  previousValue: number | null;
  changePercent: number | null;
  direction: AnalyticsDirection;
}

export interface AnalyticsTrendPoint {
  timestamp: string;
  value: number;
  previousValue: number | null;
}

export interface AnalyticsBreakdown {
  code: string;
  label: string | null;
  value: number;
  previousValue: number | null;
  changePercent: number | null;
}

export interface AnalyticsInsight {
  code: string;
  severity: AnalyticsSeverity;
  value: number | null;
  context: Record<string, string | number | boolean | null>;
}

export interface GuildAnalyticsResponse {
  generatedAt: string;
  period: AnalyticsPeriod;
  kpis: AnalyticsKpi[];
  trend: AnalyticsTrendPoint[];
  breakdown: AnalyticsBreakdown[];
  insights: AnalyticsInsight[];
}

export type GuildAnalyticsOverview = GuildAnalyticsResponse;
export type GuildAnalyticsXp = GuildAnalyticsResponse;
export type GuildAnalyticsVoice = GuildAnalyticsResponse;
export type GuildAnalyticsFeatures = GuildAnalyticsResponse;

export type AnalyticsTimelineRange = '7d' | '30d' | '90d' | 'custom';
export type AnalyticsBucketSize = 'auto' | 'day' | 'week';
export interface AnalyticsMetricValues { total: number; voice: number; messages: number; reactions: number; other: number; }
export interface AnalyticsActivityValues { total: number; voice: number; messages: number; reactions: number; other: number; }
export interface AnalyticsTimelineAggregate { activeMembers: number; activeVoiceMembers: number; qualifiedVoiceSeconds: number; awardedXp: AnalyticsMetricValues; activities: AnalyticsActivityValues; levelUps: number; }
export interface AnalyticsTimelineBucket extends AnalyticsTimelineAggregate { start: string; end: string; isIncomplete: boolean; }
export interface GuildAnalyticsTimeline {
  rangeStart: string;
  rangeEnd: string;
  timeZone: string;
  bucketSize: 'day' | 'week';
  generatedAt: string;
  summary: { current: AnalyticsTimelineAggregate; previous: AnalyticsTimelineAggregate };
  buckets: AnalyticsTimelineBucket[];
  previousBuckets: AnalyticsTimelineBucket[];
}

export interface AnalyticsTimelineQuery {
  range: AnalyticsTimelineRange;
  bucket: AnalyticsBucketSize;
  from?: string;
  to?: string;
}

export interface AnalyticsAuditItem {
  id: string;
  occurredAt: string;
  code: string;
  actorId: string | null;
  actorName: string | null;
  subjectId: string | null;
  subjectName: string | null;
  outcome: string;
  correlationId: string | null;
  metadata: Record<string, string | number | boolean | null>;
  channelId: string | null;
  channelName: string | null;
  hubName: string | null;
}

export interface GuildAnalyticsAudit extends GuildAnalyticsResponse {
  items: AnalyticsAuditItem[];
  nextCursor: string | null;
}
