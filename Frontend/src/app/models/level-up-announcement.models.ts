export type RewardRoleRequirement = 'Any' | 'Required' | 'NotAwarded';
export interface LevelUpMessageTemplate { id: string; name: string; content: string; contents: string[]; enabled: boolean; priority: number; weight: number; minimumLevel: number | null; maximumLevel: number | null; everyNthLevel: number | null; exactLevels: number[]; rewardRoleRequirement: RewardRoleRequirement; sources: string[]; }
export interface LevelUpAnnouncementSettings { guildId?: string; enabled: boolean; channelId: string | null; notifyMentionedUser: boolean; useDefaultFallback: boolean; fallbackLocale: string; announceManualAdjustments: boolean; avoidRecentTemplatesPerUser: number; templates: LevelUpMessageTemplate[]; revision: number; updatedAtUtc?: string; }
export interface LevelUpAnnouncementResponse { settings: LevelUpAnnouncementSettings; legacyChannelMigrated: boolean; channelStatus: { exists: boolean; canSend: boolean; }; }
export interface TemplateToken { name: string; requiresRewardRole: boolean; }
export interface TemplateSchema { maximumTemplateLength: number; maximumRenderedLength: number; tokens: TemplateToken[]; }
export interface LevelUpPreviewRequest { template: LevelUpMessageTemplate; displayName?: string; username?: string; level: number; previousLevel?: number; totalXp?: number; gainedXp?: number; source?: string; rewardRoleAwarded: boolean; variationIndex?: number; }
export interface PreviewUserMention { id: string; displayName: string; username: string; }
export interface LevelUpPreviewResponse { content: string | null; tokens: string[]; validationErrors: { field: string; code: string; }[]; userMentions: PreviewUserMention[]; }
