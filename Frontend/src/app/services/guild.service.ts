import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface RankEntry { userId: string; displayName: string; totalXp: string | number; level: number; messageCount: string | number; voiceSeconds: string | number; }
export interface DashboardData { guildName: string; memberCount: string | number; activeVoiceMembers: number; activeXpMembers: string | number; stats: { xpAwarded: string | number; messages: string | number; reactions: string | number; threads: string | number; eventInterests: string | number; temporaryChannelsCreated: string | number }; activeTemporaryChannels: string | number; processUptimeSeconds: number; watchdog: { state: string; lastRunAt: string | null; lastError: string | null }; leaderboard: RankEntry[]; }
export interface XpConfig { enabled: boolean; message: { enabled: boolean; minimumPoints: number; maximumPoints: number; minimumCharacters: number; maximumCharacters: number; cooldownSeconds: number }; voice: { enabled: boolean; pointsPerMinute: number; minimumSessionSeconds: number; checkIntervalSeconds: number; requireMultipleHumans: boolean; excludeAfkChannel: boolean; holdbackThreshold: number }; reaction: { enabled: boolean; points: number; cooldownSeconds: number; reverseOnRemove: boolean }; eventInterest: { enabled: boolean; points: number }; thread: { enabled: boolean; createPoints: number; messagePoints: number; cooldownSeconds: number }; excludedChannelIds: string[]; excludedCategoryIds: string[]; excludedRoleIds: string[]; channelMultipliers: { channelId: string; multiplier: number }[]; levelRoles: { level: number; roleId: string }[]; levelUpChannelId: string | null; }
export interface VcHub { id?: string; guildId?: string; joinChannelId: string; hubChannelName: string; categoryId: string | null; nameTemplate: string; userLimit: number; bitrate: number; maxChannelsPerOwner: number; enabled: boolean; }
export interface GuildResources { roles: { id: string; name: string }[]; channels: { id: string; name: string; type: string }[]; }

@Injectable({ providedIn: 'root' })
export class GuildService {
  private readonly http = inject(HttpClient);
  private url(guildId: string, path: string): string { return `${environment.apiBaseUrl}/guilds/${guildId}/${path}`; }
  dashboard(guildId: string): Observable<DashboardData> { return this.http.get<DashboardData>(this.url(guildId, 'dashboard')); }
  resources(guildId: string): Observable<GuildResources> { return this.http.get<GuildResources>(this.url(guildId, 'resources')); }
  config(guildId: string): Observable<XpConfig> { return this.http.get<XpConfig>(this.url(guildId, 'xp/config')); }
  saveConfig(guildId: string, config: XpConfig): Observable<XpConfig> { return this.http.put<XpConfig>(this.url(guildId, 'xp/config'), config); }
  leaderboard(guildId: string): Observable<RankEntry[]> { return this.http.get<RankEntry[]>(this.url(guildId, 'xp/leaderboard')); }
  importMee6(guildId: string, data: unknown): Observable<{ imported: number }> { return this.http.post<{ imported: number }>(this.url(guildId, 'xp/import/mee6'), data); }
  hubs(guildId: string): Observable<VcHub[]> { return this.http.get<VcHub[]>(this.url(guildId, 'vc-hubs')); }
  createHub(guildId: string, hub: VcHub): Observable<VcHub> { return this.http.post<VcHub>(this.url(guildId, 'vc-hubs'), hub); }
  updateHub(guildId: string, hub: VcHub): Observable<VcHub> { return this.http.put<VcHub>(this.url(guildId, `vc-hubs/${hub.id}`), hub); }
  deleteHub(guildId: string, hubId: string): Observable<void> { return this.http.delete<void>(this.url(guildId, `vc-hubs/${hubId}`)); }
}
