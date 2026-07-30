export type LevelProgressScope = 'Lifetime' | 'Season';
export type LevelAnnouncementKind = 'LevelUp' | 'Reward';
export interface LevelAnnouncementConditions { minimumLevel: number | null; maximumLevel: number | null; everyNthLevel: number | null; exactLevels: number[]; sources: string[]; }
export interface LevelAnnouncementMessage { id: string; enabled: boolean; content: string; }
export interface LevelAnnouncementGroup { id: string; name: string; enabled: boolean; weight: number; conditions: LevelAnnouncementConditions; messages: LevelAnnouncementMessage[]; }
export interface LevelAnnouncementSet { groups: LevelAnnouncementGroup[]; }
export interface LevelAnnouncementProfile { enabled: boolean; channelId: string | null; notifyUser: boolean; announceManualAdjustments: boolean; avoidRecentMessagesPerUser: number; useDefaultFallback: boolean; fallbackLocale: string; levelUp: LevelAnnouncementSet; rewards: LevelAnnouncementSet; }
export interface LevelUpAnnouncementSettings { guildId?: string; schemaVersion: number; lifetime: LevelAnnouncementProfile; season: LevelAnnouncementProfile; revision: number; updatedAtUtc?: string; }
export interface LevelUpAnnouncementResponse { settings: LevelUpAnnouncementSettings; migrated: boolean; channelStatus: { lifetime: { exists: boolean; canSend: boolean }; season: { exists: boolean; canSend: boolean } }; }
export interface TemplateToken { name: string; requiresRewardRole: boolean; requiresSeason: boolean; }
export interface TemplateSchema { maximumTemplateLength: number; maximumRenderedLength: number; tokens: TemplateToken[]; }
export interface LevelUpPreviewRequest { scope: LevelProgressScope; kind: LevelAnnouncementKind; group?: LevelAnnouncementGroup; message?: LevelAnnouncementMessage; displayName?: string; username?: string; level: number; previousLevel?: number; totalXp?: number; gainedXp?: number; source?: string; rewardRoleAwarded: boolean; seasonName?: string; }
export interface LevelUpPreviewResponse { content: string | null; tokens: string[]; validationErrors: { field: string; code: string; }[]; }
