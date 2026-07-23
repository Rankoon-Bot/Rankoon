export type BotManagementRange = '24h' | '7d' | '30d' | '90d';
export type BotManagementStatus = 'veryActive' | 'active' | 'lowActivity' | 'inactive' | 'new' | 'attentionRequired';

export interface BotManagementGuild {
  guildId: string; name: string; iconUrl: string | null; memberCount: number; botJoinedAt: string | null; lastActivityAt: string | null;
  activityEventCount: number; commandEventCount: number; errorEventCount: number; failedEventCount: number; uniqueActorCount: number;
  activeDayCount: number; activityPerHundredMembers: number; status: BotManagementStatus;
}
export interface BotManagementOverview {
  generatedAt: string; range: { key: BotManagementRange; from: string; to: string };
  summary: { connectedGuildCount: number; summedMemberCount: number; activeGuildCount: number; activityEventCount: number; commandEventCount: number; errorEventCount: number };
  guilds: BotManagementGuild[];
}
