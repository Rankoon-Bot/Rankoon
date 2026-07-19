import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface RankEntry { userId: string; displayName: string; totalXp: string | number; level: number; messageCount: string | number; voiceSeconds: string | number; }
export interface DashboardData { guildName: string; leaderboardAlias: string; memberCount: string | number; botCount: string | number; activeVoiceMembers: number; activeXpMembers: string | number; stats: { xpAwarded: string | number; messages: string | number; reactions: string | number; threads: string | number; eventInterests: string | number; temporaryChannelsCreated: string | number }; activeTemporaryChannels: string | number; processUptimeSeconds: number; watchdog: { state: string; lastRunAt: string | null; lastError: string | null }; leaderboard: RankEntry[]; }
export interface XpConfig { enabled: boolean; message: { enabled: boolean; minimumPoints: number; maximumPoints: number; minimumCharacters: number; maximumCharacters: number; cooldownSeconds: number }; voice: { enabled: boolean; pointsPerMinute: number; minimumSessionSeconds: number; checkIntervalSeconds: number; requireMultipleHumans: boolean; excludeAfkChannel: boolean; holdbackThreshold: number }; reaction: { enabled: boolean; points: number; cooldownSeconds: number; reverseOnRemove: boolean }; eventInterest: { enabled: boolean; points: number }; thread: { enabled: boolean; createPoints: number; messagePoints: number; cooldownSeconds: number }; excludedChannelIds: string[]; excludedCategoryIds: string[]; excludedRoleIds: string[]; channelMultipliers: { channelId: string; multiplier: number }[]; levelRoles: { level: number; roleId: string }[]; levelUpChannelId: string | null; }
export interface VoiceWatchdogStatus { guildId: string | number; state: string | number; lastRunAt: string | null; lastPersistenceAt: string | null; connectedUsers: string | number; eligibleUsers: string | number; excludedUsers: string | number; lastError: string | null; }
export interface VoiceWatchdogResponse { settings: XpConfig; status: VoiceWatchdogStatus; }
export interface VcHub { id?: string; guildId?: string; joinChannelId: number; hubChannelName: string; categoryId: string | null; nameTemplate: string; userLimit: number; bitrate: number; maxChannelsPerOwner: number; enabled: boolean; }
export interface GuildResources { roles: { id: string; name: string }[]; channels: { id: string; name: string; type: string }[]; }
export type LeaderboardVisibility = 'Public' | 'MembersOnly';
export interface LeaderboardSettings { guildId: string; alias: string; visibility: LeaderboardVisibility; updatedAt: string; }
export interface LeaderboardEntry extends RankEntry { rank: number; isCurrentUser: boolean; }
export interface LeaderboardPage { guildName: string; alias: string; visibility: LeaderboardVisibility; items: LeaderboardEntry[]; nextCursor: string | null; hasMore: boolean; isMember: boolean; publicVisible: boolean | null; }

@Injectable({ providedIn: 'root' })
export class GuildService {
  private readonly http = inject(HttpClient);
  private url(guildId: string, path: string): string { return `${environment.apiBaseUrl}/guilds/${guildId}/${path}`; }
  dashboard(guildId: string): Observable<DashboardData> { return this.http.get<DashboardData>(this.url(guildId, 'dashboard')); }
  resources(guildId: string): Observable<GuildResources> { return this.http.get<GuildResources>(this.url(guildId, 'resources')); }
  config(guildId: string): Observable<XpConfig> { return this.http.get<XpConfig>(this.url(guildId, 'xp/config')); }
  saveConfig(guildId: string, config: XpConfig): Observable<XpConfig> { return this.http.put<XpConfig>(this.url(guildId, 'xp/config'), config); }
  voiceWatchdog(guildId: string): Observable<VoiceWatchdogStatus> { return this.http.get<VoiceWatchdogStatus>(this.url(guildId, 'xp/watchdog')); }
  setVoiceWatchdog(guildId: string, enabled: boolean): Observable<VoiceWatchdogResponse> { return this.http.put<VoiceWatchdogResponse>(this.url(guildId, 'xp/watchdog'), { enabled }); }
  leaderboard(guildId: string): Observable<RankEntry[]> { return this.http.get<RankEntry[]>(this.url(guildId, 'xp/leaderboard')); }
  leaderboardSettings(guildId: string): Observable<LeaderboardSettings> { return this.http.get<LeaderboardSettings>(this.url(guildId, 'leaderboard-settings')); }
  saveLeaderboardSettings(guildId: string, settings: Pick<LeaderboardSettings, 'alias' | 'visibility'>): Observable<LeaderboardSettings> { return this.http.put<LeaderboardSettings>(this.url(guildId, 'leaderboard-settings'), settings); }
  publicLeaderboard(alias: string, cursor?: string, aroundMe = false): Observable<LeaderboardPage> {
    const params: Record<string, string> = { take: '25' };
    if (cursor) params['cursor'] = cursor;
    if (aroundMe) params['aroundMe'] = 'true';
    return this.http.get<LeaderboardPage>(`${environment.apiBaseUrl}/rankings/${encodeURIComponent(alias)}`, { params });
  }
  setLeaderboardPrivacy(alias: string, publicVisible: boolean): Observable<{ publicVisible: boolean }> { return this.http.put<{ publicVisible: boolean }>(`${environment.apiBaseUrl}/rankings/${encodeURIComponent(alias)}/me/privacy`, { publicVisible }); }
  importMee6(guildId: string, data: unknown): Observable<{ imported: number }> { return this.http.post<{ imported: number }>(this.url(guildId, 'xp/import/mee6'), data); }
  hubs(guildId: string): Observable<VcHub[]> { return this.http.get<VcHub[]>(this.url(guildId, 'vc-hubs')); }
  createHub(guildId: string, hub: VcHub): Observable<VcHub> { return this.http.post<VcHub>(this.url(guildId, 'vc-hubs'), hub); }
  updateHub(guildId: string, hub: VcHub): Observable<VcHub> { return this.http.put<VcHub>(this.url(guildId, `vc-hubs/${hub.id}`), hub); }
  deleteHub(guildId: string, hubId: string): Observable<void> { return this.http.delete<void>(this.url(guildId, `vc-hubs/${hubId}`)); }
}
