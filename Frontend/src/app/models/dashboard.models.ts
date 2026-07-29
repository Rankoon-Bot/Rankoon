export type DashboardPeriod = 'SevenDays' | 'ThirtyDays';
export type DashboardStatus = 'Healthy' | 'Warning' | 'Critical' | 'Disabled' | 'SetupRequired' | 'Unknown';

export interface DashboardOverview {
  generatedAtUtc: string;
  period: DashboardPeriod;
  periodStartUtc: string;
  periodEndUtc: string;
  guild: { guildId: string; name: string; iconUrl: string | null; memberCount: number; botCount: number; liveVoiceMemberCount: number };
  bot: { identityMode: string; displayName: string | null; avatarUrl: string | null; connected: boolean; status: DashboardStatus; lastReadyAtUtc: string | null; statusReasonKey: string | null };
  health: { overallStatus: DashboardStatus; healthyModuleCount: number; disabledModuleCount: number; setupRequiredCount: number; warningCount: number; criticalCount: number; unknownCount: number; attentionItems: DashboardAttentionItem[] };
  activity: { activeMemberCount: number; xpAwarded: number; voiceSeconds: number; qualifiedActivityCount: number; temporaryChannelsCreated: number; sources: DashboardActivitySource[]; trend: DashboardTrendPoint[] };
  modules: DashboardModule[];
  recentEvents: DashboardRecentEvent[];
}
export interface DashboardAttentionItem { key: string; severity: 'Info' | 'Warning' | 'Critical'; moduleId: string; titleKey: string; descriptionKey: string; parameters: Record<string, string>; action: { actionKey: string; moduleId: string; resourceId: string | null } | null; }
export interface DashboardActivitySource { source: string; eventCount: number; xpAwarded: number; percentage: number; }
export interface DashboardTrendPoint { dateUtc: string; xpAwarded: number; activeMemberCount: number; voiceSeconds: number; qualifiedActivityCount: number; }
export interface DashboardModule { moduleId: string; enabled: boolean; status: DashboardStatus; statusReasonKey: string; statusParameters: Record<string, string>; metrics: { key: string; value: string }[]; lastActivityAtUtc: string | null; }
export interface DashboardRecentEvent { id: string; name: string; outcome: string; severity: string | null; moduleId: string | null; actorId: string | null; actorDisplayName: string | null; subjectId: string | null; subjectDisplayName: string | null; channelId: string | null; channelName: string | null; parameters: Record<string, string>; occurredAtUtc: string; }
