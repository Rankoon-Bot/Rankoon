export type BotManagementRange = '24h' | '7d' | '30d' | '90d';
export type BotManagementStatus = 'veryActive' | 'active' | 'lowActivity' | 'inactive' | 'new' | 'attentionRequired';
export type IncidentStatus = 'open' | 'acknowledged' | 'resolved';
export type IncidentSeverity = 'warning' | 'error' | 'critical';

export interface BotManagementGuild {
  guildId: string; name: string; iconUrl: string | null; memberCount: number; botJoinedAt: string | null; lastActivityAt: string | null;
  activityEventCount: number; commandEventCount: number; errorEventCount: number; failedEventCount: number; uniqueActorCount: number;
  activeDayCount: number; activityPerHundredMembers: number; status: BotManagementStatus;
}
export interface OperationsMetric { code: string; value: number; previousValue: number | null; changePercent: number | null; }
export interface OperationsTrendPoint { timestamp: string; value: number; previousValue: number | null; }
export interface OperationsInsight { code: string; severity: 'info' | 'success' | 'warning' | 'critical'; context: Record<string, string | number | boolean | null>; }
export interface OperationsOverview {
  generatedAt: string; range: { key: BotManagementRange; from: string; to: string };
  metrics: OperationsMetric[]; trend: OperationsTrendPoint[]; insights: OperationsInsight[];
}
export interface GuildHealthResponse { generatedAt: string; range: { key: BotManagementRange; from: string; to: string }; summary: OperationsMetric[]; guilds: BotManagementGuild[]; }
export interface GlobalUsageBreakdown { code: string; label: string | null; value: number; previousValue: number | null; }
export interface GlobalUsageResponse { generatedAt: string; range: { key: BotManagementRange; from: string; to: string }; metrics: OperationsMetric[]; trend: OperationsTrendPoint[]; breakdown: GlobalUsageBreakdown[]; }

export interface IncidentOccurrence { id: string; occurredAt: string; message: string; guildId: string | null; guildName: string | null; correlationId: string | null; stackTrace: string | null; }
export interface BotIncident {
  id: string; code: string; title: string; source: string; severity: IncidentSeverity; status: IncidentStatus;
  occurrenceCount: number; affectedGuildCount: number; firstSeenAt: string; lastSeenAt: string;
  acknowledgedAt: string | null; acknowledgedBy: string | null; resolvedAt: string | null; resolvedBy: string | null;
  occurrences: IncidentOccurrence[];
}
export interface IncidentQuery { range: BotManagementRange; status?: IncidentStatus; severity?: IncidentSeverity; search?: string; cursor?: string; }
export interface IncidentResponse { generatedAt: string; summary: OperationsMetric[]; items: BotIncident[]; nextCursor: string | null; }
