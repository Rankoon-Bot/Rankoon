import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  GuildCapabilities,
  RolePermissions,
  SaveRolePermissions
} from '../models/guild-permissions.models';

export interface RankEntry { userId: string; displayName: string; totalXp: string | number; level: number; messageCount: string | number; voiceSeconds: string | number; }
export interface DashboardData { guildName: string; leaderboardAlias: string; memberCount: string | number; botCount: string | number; activeVoiceMembers: number; activeXpMembers: string | number; stats: { xpAwarded: string | number; messages: string | number; reactions: string | number; threads: string | number; eventInterests: string | number; temporaryChannelsCreated: string | number }; activeTemporaryChannels: string | number; processUptimeSeconds: number; watchdog: { state: string; lastRunAt: string | null; lastError: string | null }; leaderboard: RankEntry[]; }
export interface XpConfig { enabled: boolean; message: { enabled: boolean; minimumPoints: number; maximumPoints: number; minimumCharacters: number; maximumCharacters: number; cooldownSeconds: number }; voice: { enabled: boolean; pointsPerMinute: number; minimumSessionSeconds: number; requireMultipleHumans: boolean; excludeAfkChannel: boolean }; reaction: { enabled: boolean; points: number; cooldownSeconds: number; reverseOnRemove: boolean }; eventInterest: { enabled: boolean; points: number }; thread: { enabled: boolean; createPoints: number; messagePoints: number; cooldownSeconds: number }; excludedChannelIds: string[]; excludedCategoryIds: string[]; excludedRoleIds: string[]; channelMultipliers: { channelId: string; multiplier: number }[]; levelRoles: { level: number; roleId: string }[]; levelUpChannelId: string | null; }
export interface VoiceWatchdogStatus { guildId: string | number; state: string | number; lastRunAt: string | null; lastPersistenceAt: string | null; connectedUsers: string | number; eligibleUsers: string | number; excludedUsers: string | number; lastError: string | null; intervalSeconds: number; }
export interface VoiceWatchdogResponse { settings: XpConfig; status: VoiceWatchdogStatus; }
export interface VcHub { id?: string; guildId?: string; joinChannelId: number; hubChannelName: string; categoryId: string | null; nameTemplate: string; userLimit: number; bitrate: number; maxChannelsPerOwner: number; enabled: boolean; }
export interface GuildResources { roles: { id: string; name: string }[]; channels: { id: string; name: string; type: string }[]; }
export interface SelfRoleEmoji { kind: 'Unicode' | 'Custom'; value: string; name: string; }
export interface SelfRoleMapping { id?: string; emoji: SelfRoleEmoji; roleId: string; }
export interface SelfRolePanel { id?: string; guildId?: string; channelId: string; title: string; description: string; color: string; enabled: boolean; mappings: SelfRoleMapping[]; revision: number; updatedAt?: string; status?: string; }
export interface SelfRoleResources extends GuildResources { emojis: { id: string; name: string; animated: boolean; url: string; available: boolean }[]; }
export type LeaderboardVisibility = 'Public' | 'MembersOnly';
export interface LeaderboardSettings { guildId: string; alias: string; visibility: LeaderboardVisibility; updatedAt: string; }
export interface LeaderboardEntry extends RankEntry { rank: number; isCurrentUser: boolean; }
export interface SeasonLeaderboardOption { id: string; name: string; startsAtUtc: string; endsAtUtc: string; }
export interface LeaderboardPage { guildName: string; alias: string; visibility: LeaderboardVisibility; items: LeaderboardEntry[]; nextCursor: string | null; hasMore: boolean; isMember: boolean; publicVisible: boolean | null; scope?: SeasonLeaderboardScope; seasonId?: string | null; seasonName?: string | null; historicalSeasons?: SeasonLeaderboardOption[]; currentSeason?: SeasonLeaderboardOption | null; seasonsEnabled?: boolean; }
export type SeasonScheduleKind = 'Manual' | 'FixedDuration' | 'Monthly' | 'Quarterly' | 'SemiAnnual' | 'Annual';
export type SeasonStatus = 'Scheduled' | 'Active' | 'Closing' | 'Closed' | 'Cancelled';
export type SeasonLeaderboardScope = 'Lifetime' | 'CurrentSeason' | 'Season';
export type SeasonInitialXpMode = 'Zero' | 'Lifetime' | 'LifetimePercentage';
export type SeasonCarryOverMode = 'None' | 'Percentage';
export interface SeasonLevelRole { level: number; roleId: string; retention: 'RemoveAtSeasonEnd' | 'Keep'; }
export interface SeasonAnnouncements { startEnabled: boolean; endEnabled: boolean; winnerEnabled: boolean; warningOffsetsMinutes: number[]; }
export interface SeasonSettings {
  guildId?: string; enabled: boolean; defaultLeaderboardScope: SeasonLeaderboardScope; timeZoneId: string; scheduleKind: SeasonScheduleKind;
  scheduleAnchorUtc: string | null; fixedDurationDays: number | null; gapDays: number; preparedSeasonCount: number; pauseBehavior: string;
  publicHistoryCount: number; initialXpMode: SeasonInitialXpMode; initialXpPercentage: number; carryOverMode: SeasonCarryOverMode;
  carryOverPercentage: number; carryOverMaximumXp: number | null; announcementChannelId: string | null; announcements: SeasonAnnouncements;
  winnerCount: number; nameTemplate: string; rotation: string[]; rotationOffset: number; seasonLevelRoles: SeasonLevelRole[]; revision?: number;
}
export interface Season { id?: string; guildId?: string; sequence: number; name: string; description: string | null; status: SeasonStatus; startsAtUtc: string; endsAtUtc: string; createdAtUtc?: string; activatedAtUtc?: string | null; closedAtUtc?: string | null; previousSeasonId?: string | null; scheduleRevision?: number; carryOverApplied?: boolean; finalized?: boolean; }
export interface SeasonPreview { sequence: number; startsAtUtc: string; endsAtUtc: string; name: string; }

@Injectable({ providedIn: 'root' })
export class GuildService {
  private readonly http = inject(HttpClient);
  private url(guildId: string, path: string): string { return `${environment.apiBaseUrl}/guilds/${guildId}/${path}`; }
  dashboard(guildId: string): Observable<DashboardData> { return this.http.get<DashboardData>(this.url(guildId, 'dashboard')); }
  capabilities(guildId: string): Observable<GuildCapabilities> { return this.http.get<GuildCapabilities>(this.url(guildId, 'capabilities')); }
  rolePermissions(guildId: string): Observable<RolePermissions> { return this.http.get<RolePermissions>(this.url(guildId, 'role-permissions')); }
  saveRolePermissions(guildId: string, permissions: SaveRolePermissions): Observable<RolePermissions> { return this.http.put<RolePermissions>(this.url(guildId, 'role-permissions'), permissions); }
  resources(guildId: string): Observable<GuildResources> { return this.http.get<GuildResources>(this.url(guildId, 'resources')); }
  config(guildId: string): Observable<XpConfig> { return this.http.get<XpConfig>(this.url(guildId, 'xp/config')); }
  saveConfig(guildId: string, config: XpConfig): Observable<XpConfig> { return this.http.put<XpConfig>(this.url(guildId, 'xp/config'), config); }
  voiceWatchdog(guildId: string): Observable<VoiceWatchdogStatus> { return this.http.get<VoiceWatchdogStatus>(this.url(guildId, 'xp/watchdog')); }
  setVoiceWatchdog(guildId: string, enabled: boolean): Observable<VoiceWatchdogResponse> { return this.http.put<VoiceWatchdogResponse>(this.url(guildId, 'xp/watchdog'), { enabled }); }
  leaderboard(guildId: string): Observable<RankEntry[]> { return this.http.get<RankEntry[]>(this.url(guildId, 'xp/leaderboard')); }
  seasonConfig(guildId: string): Observable<SeasonSettings> { return this.http.get<SeasonSettings>(this.url(guildId, 'xp/seasons/config')); }
  saveSeasonConfig(guildId: string, settings: SeasonSettings): Observable<SeasonSettings> { return this.http.put<SeasonSettings>(this.url(guildId, 'xp/seasons/config'), settings); }
  previewSeasons(guildId: string, settings: SeasonSettings, count = 6): Observable<SeasonPreview[]> { return this.http.post<SeasonPreview[]>(`${this.url(guildId, 'xp/seasons/preview')}?count=${count}`, settings); }
  seasons(guildId: string): Observable<Season[]> { return this.http.get<Season[]>(this.url(guildId, 'xp/seasons')); }
  currentSeason(guildId: string): Observable<Season> { return this.http.get<Season>(this.url(guildId, 'xp/seasons/current')); }
  createSeason(guildId: string, season: Season): Observable<Season> { return this.http.post<Season>(this.url(guildId, 'xp/seasons'), season); }
  planSeasons(guildId: string, count: number): Observable<Season[]> { return this.http.post<Season[]>(this.url(guildId, 'xp/seasons/plan'), { count }); }
  updateSeason(guildId: string, season: Season): Observable<Season> { return this.http.put<Season>(this.url(guildId, `xp/seasons/${season.id}`), season); }
  startSeason(guildId: string, seasonId: string): Observable<Season> { return this.http.post<Season>(this.url(guildId, `xp/seasons/${seasonId}/start`), {}); }
  closeSeason(guildId: string, seasonId: string): Observable<Season> { return this.http.post<Season>(this.url(guildId, `xp/seasons/${seasonId}/close`), {}); }
  cancelSeason(guildId: string, seasonId: string): Observable<Season> { return this.http.post<Season>(this.url(guildId, `xp/seasons/${seasonId}/cancel`), {}); }
  resumeSeason(guildId: string, seasonId: string): Observable<Season> { return this.http.post<Season>(this.url(guildId, `xp/seasons/${seasonId}/resume`), {}); }
  deleteSeason(guildId: string, seasonId: string): Observable<void> { return this.http.delete<void>(this.url(guildId, `xp/seasons/${seasonId}`)); }
  leaderboardSettings(guildId: string): Observable<LeaderboardSettings> { return this.http.get<LeaderboardSettings>(this.url(guildId, 'leaderboard-settings')); }
  saveLeaderboardSettings(guildId: string, settings: Pick<LeaderboardSettings, 'alias' | 'visibility'>): Observable<LeaderboardSettings> { return this.http.put<LeaderboardSettings>(this.url(guildId, 'leaderboard-settings'), settings); }
  publicLeaderboard(alias: string, cursor?: string, aroundMe = false, scope?: SeasonLeaderboardScope, seasonId?: string): Observable<LeaderboardPage> {
    const params: Record<string, string> = { take: '25' };
    if (cursor) params['cursor'] = cursor;
    if (aroundMe) params['aroundMe'] = 'true';
    if (scope) params['scope'] = scope;
    if (seasonId) params['seasonId'] = seasonId;
    return this.http.get<LeaderboardPage>(`${environment.apiBaseUrl}/rankings/${encodeURIComponent(alias)}`, { params });
  }
  setLeaderboardPrivacy(alias: string, publicVisible: boolean): Observable<{ publicVisible: boolean }> { return this.http.put<{ publicVisible: boolean }>(`${environment.apiBaseUrl}/rankings/${encodeURIComponent(alias)}/me/privacy`, { publicVisible }); }
  importMee6(guildId: string, data: unknown): Observable<{ imported: number }> { return this.http.post<{ imported: number }>(this.url(guildId, 'xp/import/mee6'), data); }
  hubs(guildId: string): Observable<VcHub[]> { return this.http.get<VcHub[]>(this.url(guildId, 'vc-hubs')); }
  createHub(guildId: string, hub: VcHub): Observable<VcHub> { return this.http.post<VcHub>(this.url(guildId, 'vc-hubs'), hub); }
  updateHub(guildId: string, hub: VcHub): Observable<VcHub> { return this.http.put<VcHub>(this.url(guildId, `vc-hubs/${hub.id}`), hub); }
  deleteHub(guildId: string, hubId: string): Observable<void> { return this.http.delete<void>(this.url(guildId, `vc-hubs/${hubId}`)); }
  selfRolePanels(guildId: string): Observable<SelfRolePanel[]> { return this.http.get<SelfRolePanel[]>(this.url(guildId, 'self-role-panels')); }
  selfRoleResources(guildId: string): Observable<SelfRoleResources> { return this.http.get<SelfRoleResources>(this.url(guildId, 'self-role-resources')); }
  createSelfRolePanel(guildId: string, panel: SelfRolePanel): Observable<SelfRolePanel> { return this.http.post<SelfRolePanel>(this.url(guildId, 'self-role-panels'), panel); }
  updateSelfRolePanel(guildId: string, panel: SelfRolePanel): Observable<SelfRolePanel> { return this.http.put<SelfRolePanel>(this.url(guildId, `self-role-panels/${panel.id}`), panel); }
  deleteSelfRolePanel(guildId: string, panelId: string): Observable<void> { return this.http.delete<void>(this.url(guildId, `self-role-panels/${panelId}`)); }
}
