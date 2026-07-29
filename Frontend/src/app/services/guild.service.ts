import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { finalize, shareReplay, tap } from 'rxjs/operators';
import { environment } from '../../environments/environment';
import {
  GuildCapabilities,
  RolePermissions,
  SaveRolePermissions,
} from '../models/guild-permissions.models';
import {
  LevelUpAnnouncementResponse,
  LevelUpAnnouncementSettings,
  LevelUpPreviewRequest,
  LevelUpPreviewResponse,
  TemplateSchema,
} from '../models/level-up-announcement.models';
import {
  ChannelDiagnostic,
  PermissionDiagnosticReport,
  PermissionDiagnosticScope,
} from '../models/permission-diagnostics.models';

export interface RankEntry {
  userId: string;
  displayName: string;
  iconUrl?: string | null;
  totalXp: string | number;
  level: number;
  messageCount: string | number;
  voiceSeconds: string | number;
}
export type XpImportFormat = 'Mee6' | 'CustomRankoon';
export interface XpImportResult {
  format: XpImportFormat;
  imported: number;
  skippedInvalid: number;
  skippedForeignGuild: number;
  duplicateUsers: number;
}
export interface ServerBoosterXpTier {
  minimumBoostMonths: number;
  multiplier: number;
}
export interface LevelRoleReward {
  level: number;
  roleId: string;
  description?: string | null;
}
export interface XpConfig {
  enabled: boolean;
  message: {
    enabled: boolean;
    minimumPoints: number;
    maximumPoints: number;
    minimumCharacters: number;
    maximumCharacters: number;
    cooldownSeconds: number;
  };
  voice: {
    enabled: boolean;
    pointsPerMinute: number;
    minimumSessionSeconds: number;
    settingsVersion: number;
    eligibility: {
      awardWhileSelfMuted: boolean;
      awardWhileSelfDeafened: boolean;
      awardWhileGuildMuted: boolean;
      awardWhileGuildDeafened: boolean;
      awardWhileSuppressed: boolean;
      awardInAfkChannel: boolean;
      minimumHumanParticipants: number;
      participantCountingMode: 'AllConnectedHumans' | 'EligibleHumansOnly';
      resetMinimumSessionWhenIneligible: boolean;
    };
  };
  reaction: {
    enabled: boolean;
    points: number;
    cooldownSeconds: number;
    reverseOnRemove: boolean;
  };
  eventInterest: { enabled: boolean; points: number };
  thread: {
    enabled: boolean;
    createPoints: number;
    messagePoints: number;
    cooldownSeconds: number;
  };
  excludedChannelIds: string[];
  excludedCategoryIds: string[];
  excludedRoleIds: string[];
  channelMultipliers: { channelId: string; multiplier: number }[];
  serverBooster: { enabled: boolean; tiers: ServerBoosterXpTier[] };
  levelRoles: LevelRoleReward[];
  levelUpChannelId: string | null;
}
export interface VoiceWatchdogStatus {
  guildId: string | number;
  state: string | number;
  lastRunAt: string | null;
  lastPersistenceAt: string | null;
  connectedUsers: string | number;
  eligibleUsers: string | number;
  excludedUsers: string | number;
  lastError: string | null;
  intervalSeconds: number;
}
export interface VcHub {
  id?: string;
  guildId?: string;
  // Discord snowflakes exceed JavaScript's safe integer range. Keep API IDs as strings.
  joinChannelId: string | number;
  hubChannelName: string;
  categoryId: string | null;
  nameTemplate: string;
  userLimit: number;
  bitrate: number;
  maxChannelsPerOwner: number;
  enabled: boolean;
}
export interface GuildResources {
  roles: { id: string; name: string }[];
  channels: { id: string; name: string; type: string }[];
}
export interface SelfRoleEmoji {
  kind: 'Unicode' | 'Custom';
  value: string;
  name: string;
}
export interface SelfRoleMapping {
  id?: string;
  emoji: SelfRoleEmoji;
  roleId: string;
}
export type SelfRoleEmbedKind = 'Content' | 'RoleMappings';
export interface SelfRoleEmbedField {
  id?: string;
  name: string;
  value: string;
  inline: boolean;
}
export interface SelfRoleEmbed {
  id?: string;
  kind: SelfRoleEmbedKind;
  title: string;
  description: string;
  color: string;
  fields: SelfRoleEmbedField[];
}
export interface SelfRolePanel {
  id?: string;
  guildId?: string;
  channelId: string;
  embeds: SelfRoleEmbed[];
  title?: string;
  description?: string;
  color?: string;
  enabled: boolean;
  mappings: SelfRoleMapping[];
  revision: number;
  updatedAt?: string;
  status?: string;
}
export interface SelfRoleResources extends GuildResources {
  emojis: {
    id: string;
    name: string;
    animated: boolean;
    url: string;
    available: boolean;
  }[];
}
export type LeaderboardVisibility = 'Public' | 'MembersOnly';
export interface LeaderboardSettings {
  guildId: string;
  alias: string;
  visibility: LeaderboardVisibility;
  updatedAt: string;
}
export interface LeaderboardEntry extends RankEntry {
  rank: number;
  isCurrentUser: boolean;
}
export interface LeaderboardViewerCapabilities {
  guildId: string;
  canAuditXp: boolean;
  canAdjustXp: boolean;
}
export interface LeaderboardLevelReward {
  level: number;
  roleName: string;
  description?: string | null;
}
export interface SeasonLeaderboardOption {
  id: string;
  name: string;
  startsAtUtc: string;
  endsAtUtc: string;
}
export interface LeaderboardPage {
  guildName: string;
  alias: string;
  visibility: LeaderboardVisibility;
  items: LeaderboardEntry[];
  nextCursor: string | null;
  hasMore: boolean;
  isMember: boolean;
  publicVisible: boolean | null;
  scope?: SeasonLeaderboardScope;
  seasonId?: string | null;
  seasonName?: string | null;
  historicalSeasons?: SeasonLeaderboardOption[];
  currentSeason?: SeasonLeaderboardOption | null;
  seasonsEnabled?: boolean;
  viewerCapabilities?: LeaderboardViewerCapabilities | null;
  levelRewards?: LeaderboardLevelReward[];
}
export interface LeaderboardWindowRow {
  index: number;
  entry: LeaderboardEntry;
}
export interface LeaderboardWindowRequest {
  alias: string;
  scope: SeasonLeaderboardScope;
  seasonId?: string;
  offset: number;
  take: number;
  aroundCurrentUser: boolean;
  cachedUserIds: string[];
}
export interface LeaderboardWindow {
  guildName: string;
  alias: string;
  visibility: LeaderboardVisibility;
  items: LeaderboardWindowRow[];
  cachedItems: LeaderboardWindowRow[];
  removedCachedUserIds: string[];
  offset: number;
  totalCount: number;
  isMember: boolean;
  publicVisible: boolean | null;
  scope: SeasonLeaderboardScope;
  seasonId: string | null;
  seasonName: string | null;
  historicalSeasons: SeasonLeaderboardOption[];
  currentSeason: SeasonLeaderboardOption | null;
  seasonsEnabled: boolean;
  viewerCapabilities: LeaderboardViewerCapabilities | null;
}
export type SeasonScheduleKind =
  | 'Manual'
  | 'FixedDuration'
  | 'Monthly'
  | 'Quarterly'
  | 'SemiAnnual'
  | 'Annual';
export type SeasonStatus =
  'Scheduled' | 'Active' | 'Closing' | 'Closed' | 'Cancelled';
export type SeasonLeaderboardScope = 'Lifetime' | 'CurrentSeason' | 'Season';
export type SeasonInitialXpMode = 'Zero' | 'Lifetime' | 'LifetimePercentage';
export type SeasonCarryOverMode = 'None' | 'Percentage';
export interface SeasonLevelRole {
  level: number;
  roleId: string;
  retention: 'RemoveAtSeasonEnd' | 'Keep';
}
export interface SeasonAnnouncements {
  startEnabled: boolean;
  endEnabled: boolean;
  winnerEnabled: boolean;
  warningOffsetsMinutes: number[];
}
export interface SeasonSettings {
  guildId?: string;
  enabled: boolean;
  defaultLeaderboardScope: SeasonLeaderboardScope;
  timeZoneId: string;
  scheduleKind: SeasonScheduleKind;
  scheduleAnchorUtc: string | null;
  fixedDurationDays: number | null;
  gapDays: number;
  preparedSeasonCount: number;
  pauseBehavior: string;
  publicHistoryCount: number;
  initialXpMode: SeasonInitialXpMode;
  initialXpPercentage: number;
  carryOverMode: SeasonCarryOverMode;
  carryOverPercentage: number;
  carryOverMaximumXp: number | null;
  announcementChannelId: string | null;
  announcements: SeasonAnnouncements;
  winnerCount: number;
  nameTemplate: string;
  rotation: string[];
  rotationOffset: number;
  seasonLevelRoles: SeasonLevelRole[];
  revision?: number;
  numberingEpoch?: number;
}
export interface Season {
  id?: string;
  guildId?: string;
  sequence: number;
  number?: number | null;
  numberingEpoch?: number;
  name: string;
  description: string | null;
  status: SeasonStatus;
  startsAtUtc: string;
  endsAtUtc: string;
  createdAtUtc?: string;
  activatedAtUtc?: string | null;
  closedAtUtc?: string | null;
  previousSeasonId?: string | null;
  scheduleRevision?: number;
  settingsSnapshot?: Pick<SeasonSettings, 'scheduleKind'>;
  carryOverApplied?: boolean;
  finalized?: boolean;
}
export interface SeasonPreview {
  sequence: number;
  number?: number | null;
  startsAtUtc: string;
  endsAtUtc: string;
  name: string;
}
export interface SeasonBulkOperationResult {
  affectedCount: number;
}
export interface CustomBotAccess {
  isEligible: boolean;
  canActivate: boolean;
  hasReservation: boolean;
  hasConfiguredIdentity: boolean;
  activeGuilds: number;
  maximumActiveGuilds: number | null;
  reason:
    | 'Available'
    | 'AlreadyReserved'
    | 'FeatureDisabled'
    | 'GuildNotAllowed'
    | 'CapacityReached';
}
export interface CustomBotIdentity {
  guildId: string;
  mode: 'Rankoon' | 'Custom';
  status: string;
  applicationId: string | null;
  botUserId: string | null;
  botUsername: string | null;
  botGlobalName: string | null;
  botAvatarHash: string | null;
  hasStoredToken: boolean;
  lastValidatedAt: string | null;
  lastConnectedAt: string | null;
  lastReadyAt: string | null;
  lastErrorCode: string | null;
  revision: number;
  platformBotInstalled: boolean;
  customBotInstalled: boolean;
  authoritativeRuntimeAvailable: boolean;
  platformDepartureState: 'NotRequired' | 'Pending' | 'Completed' | 'Failed';
  platformDepartureErrorCode: string | null;
  platformDepartureAttemptedAt: string | null;
  platformDepartedAt: string | null;
}
export interface CustomBotOperation {
  succeeded: boolean;
  errorCode: string | null;
  identity: CustomBotIdentity | null;
  installUrl: string | null;
  diagnostics: Record<string, boolean> | null;
  warningCodes: string[] | null;
  requiredAction: string | null;
}

@Injectable({ providedIn: 'root' })
export class GuildService {
  private readonly RESOURCE_CACHE_MS = 60_000;
  private readonly http = inject(HttpClient);
  private readonly resourcesCache = new Map<string, { value: GuildResources; expiresAt: number }>();
  private readonly resourcesRequests = new Map<string, Observable<GuildResources>>();
  private readonly selfRoleResourcesCache = new Map<string, { value: SelfRoleResources; expiresAt: number }>();
  private readonly selfRoleResourcesRequests = new Map<string, Observable<SelfRoleResources>>();
  private url(guildId: string, path: string): string {
    return `${environment.apiBaseUrl}/guilds/${guildId}/${path}`;
  }
  capabilities(guildId: string): Observable<GuildCapabilities> {
    return this.http.get<GuildCapabilities>(this.url(guildId, 'capabilities'));
  }
  customBotAccess(guildId: string): Observable<CustomBotAccess> {
    return this.http.get<CustomBotAccess>(
      this.url(guildId, 'custom-bot-identity/access'),
    );
  }
  customBotIdentity(guildId: string): Observable<CustomBotIdentity | null> {
    return this.http.get<CustomBotIdentity | null>(
      this.url(guildId, 'custom-bot-identity'),
    );
  }
  storeCustomBotToken(
    guildId: string,
    token: string,
    revision?: number,
  ): Observable<CustomBotOperation> {
    return this.http.post<CustomBotOperation>(
      this.url(guildId, 'custom-bot-identity/token'),
      { token, revision },
    );
  }
  customBotInstallUrl(guildId: string): Observable<CustomBotOperation> {
    return this.http.get<CustomBotOperation>(
      this.url(guildId, 'custom-bot-identity/install-url'),
    );
  }
  validateCustomBot(guildId: string): Observable<CustomBotOperation> {
    return this.http.post<CustomBotOperation>(
      this.url(guildId, 'custom-bot-identity/validate'),
      {},
    );
  }
  activateCustomBot(
    guildId: string,
    revision?: number,
  ): Observable<CustomBotOperation> {
    return this.http.post<CustomBotOperation>(
      this.url(guildId, 'custom-bot-identity/activate'),
      { revision },
    );
  }
  restartCustomBot(guildId: string): Observable<CustomBotOperation> {
    return this.http.post<CustomBotOperation>(
      this.url(guildId, 'custom-bot-identity/restart'),
      {},
    );
  }
  completeCustomBotHandover(guildId: string): Observable<CustomBotOperation> {
    return this.http.post<CustomBotOperation>(
      this.url(guildId, 'custom-bot-identity/complete-handover'),
      {},
    );
  }
  deactivateCustomBot(guildId: string): Observable<CustomBotOperation> {
    return this.http.post<CustomBotOperation>(
      this.url(guildId, 'custom-bot-identity/deactivate'),
      {},
    );
  }
  deleteCustomBot(guildId: string): Observable<void> {
    return this.http.delete<void>(this.url(guildId, 'custom-bot-identity'));
  }
  rolePermissions(guildId: string): Observable<RolePermissions> {
    return this.http.get<RolePermissions>(
      this.url(guildId, 'role-permissions'),
    );
  }
  saveRolePermissions(
    guildId: string,
    permissions: SaveRolePermissions,
  ): Observable<RolePermissions> {
    return this.http.put<RolePermissions>(
      this.url(guildId, 'role-permissions'),
      permissions,
    );
  }
  resources(guildId: string, refresh = false): Observable<GuildResources> {
    const cached = this.resourcesCache.get(guildId);
    if (!refresh && cached && cached.expiresAt > Date.now()) return of(cached.value);

    const inFlight = this.resourcesRequests.get(guildId);
    if (inFlight) return inFlight;

    const request = this.http.get<GuildResources>(this.url(guildId, 'resources')).pipe(
      tap(value => this.resourcesCache.set(guildId, { value, expiresAt: Date.now() + this.RESOURCE_CACHE_MS })),
      finalize(() => this.resourcesRequests.delete(guildId)),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    this.resourcesRequests.set(guildId, request);
    return request;
  }
  config(guildId: string): Observable<XpConfig> {
    return this.http.get<XpConfig>(this.url(guildId, 'xp/config'));
  }
  saveConfig(guildId: string, config: XpConfig): Observable<XpConfig> {
    return this.http.put<XpConfig>(this.url(guildId, 'xp/config'), config);
  }
  voiceWatchdog(guildId: string): Observable<VoiceWatchdogStatus> {
    return this.http.get<VoiceWatchdogStatus>(this.url(guildId, 'xp/watchdog'));
  }
  leaderboard(guildId: string): Observable<RankEntry[]> {
    return this.http.get<RankEntry[]>(this.url(guildId, 'xp/leaderboard'));
  }
  seasonConfig(guildId: string): Observable<SeasonSettings> {
    return this.http.get<SeasonSettings>(
      this.url(guildId, 'xp/seasons/config'),
    );
  }
  saveSeasonConfig(
    guildId: string,
    settings: SeasonSettings,
  ): Observable<SeasonSettings> {
    return this.http.put<SeasonSettings>(
      this.url(guildId, 'xp/seasons/config'),
      settings,
    );
  }
  previewSeasons(
    guildId: string,
    settings: SeasonSettings,
    count = 6,
  ): Observable<SeasonPreview[]> {
    return this.http.post<SeasonPreview[]>(
      `${this.url(guildId, 'xp/seasons/preview')}?count=${count}`,
      settings,
    );
  }
  seasons(guildId: string): Observable<Season[]> {
    return this.http.get<Season[]>(this.url(guildId, 'xp/seasons'));
  }
  currentSeason(guildId: string): Observable<Season> {
    return this.http.get<Season>(this.url(guildId, 'xp/seasons/current'));
  }
  createSeason(guildId: string, season: Season): Observable<Season> {
    return this.http.post<Season>(this.url(guildId, 'xp/seasons'), season);
  }
  planSeasons(guildId: string, count: number): Observable<Season[]> {
    return this.http.post<Season[]>(this.url(guildId, 'xp/seasons/plan'), {
      count,
    });
  }
  updateSeason(guildId: string, season: Season): Observable<Season> {
    return this.http.put<Season>(
      this.url(guildId, `xp/seasons/${season.id}`),
      season,
    );
  }
  startSeason(guildId: string, seasonId: string): Observable<Season> {
    return this.http.post<Season>(
      this.url(guildId, `xp/seasons/${seasonId}/start`),
      {},
    );
  }
  closeSeason(guildId: string, seasonId: string): Observable<Season> {
    return this.http.post<Season>(
      this.url(guildId, `xp/seasons/${seasonId}/close`),
      {},
    );
  }
  cancelSeason(guildId: string, seasonId: string): Observable<Season> {
    return this.http.post<Season>(
      this.url(guildId, `xp/seasons/${seasonId}/cancel`),
      {},
    );
  }
  resumeSeason(guildId: string, seasonId: string): Observable<Season> {
    return this.http.post<Season>(
      this.url(guildId, `xp/seasons/${seasonId}/resume`),
      {},
    );
  }
  deleteSeason(guildId: string, seasonId: string): Observable<void> {
    return this.http.delete<void>(this.url(guildId, `xp/seasons/${seasonId}`));
  }
  resetSeasonCounter(guildId: string): Observable<SeasonBulkOperationResult> {
    return this.http.post<SeasonBulkOperationResult>(
      this.url(guildId, 'xp/seasons/counter/reset'),
      {},
    );
  }
  leaderboardSettings(guildId: string): Observable<LeaderboardSettings> {
    return this.http.get<LeaderboardSettings>(
      this.url(guildId, 'leaderboard-settings'),
    );
  }
  saveLeaderboardSettings(
    guildId: string,
    settings: Pick<LeaderboardSettings, 'alias' | 'visibility'>,
  ): Observable<LeaderboardSettings> {
    return this.http.put<LeaderboardSettings>(
      this.url(guildId, 'leaderboard-settings'),
      settings,
    );
  }
  publicLeaderboard(
    alias: string,
    cursor?: string,
    aroundMe = false,
    scope?: SeasonLeaderboardScope,
    seasonId?: string,
  ): Observable<LeaderboardPage> {
    const params: Record<string, string> = { take: '25' };
    if (cursor) params['cursor'] = cursor;
    if (aroundMe) params['aroundMe'] = 'true';
    if (scope) params['scope'] = scope;
    if (seasonId) params['seasonId'] = seasonId;
    return this.http.get<LeaderboardPage>(
      `${environment.apiBaseUrl}/rankings/${encodeURIComponent(alias)}`,
      { params },
    );
  }
  setLeaderboardPrivacy(
    alias: string,
    publicVisible: boolean,
  ): Observable<{ publicVisible: boolean }> {
    return this.http.put<{ publicVisible: boolean }>(
      `${environment.apiBaseUrl}/rankings/${encodeURIComponent(alias)}/me/privacy`,
      { publicVisible },
    );
  }
  importXpJson(guildId: string, data: unknown): Observable<XpImportResult> {
    return this.http.post<XpImportResult>(this.url(guildId, 'xp/import'), data);
  }
  levelUpAnnouncements(
    guildId: string,
  ): Observable<LevelUpAnnouncementResponse> {
    return this.http.get<LevelUpAnnouncementResponse>(
      this.url(guildId, 'xp/level-up-announcements'),
    );
  }
  saveLevelUpAnnouncements(
    guildId: string,
    settings: LevelUpAnnouncementSettings,
  ): Observable<LevelUpAnnouncementSettings> {
    return this.http.put<LevelUpAnnouncementSettings>(
      this.url(guildId, 'xp/level-up-announcements'),
      settings,
    );
  }
  levelUpTemplateSchema(guildId: string): Observable<TemplateSchema> {
    return this.http.get<TemplateSchema>(
      this.url(guildId, 'xp/level-up-announcements/template-schema'),
    );
  }
  previewLevelUpAnnouncement(
    guildId: string,
    request: LevelUpPreviewRequest,
  ): Observable<LevelUpPreviewResponse> {
    return this.http.post<LevelUpPreviewResponse>(
      this.url(guildId, 'xp/level-up-announcements/preview'),
      request,
    );
  }
  testLevelUpAnnouncement(
    guildId: string,
    request: LevelUpPreviewRequest,
  ): Observable<{ messageId: string }> {
    return this.http.post<{ messageId: string }>(
      this.url(guildId, 'xp/level-up-announcements/test'),
      request,
    );
  }
  hubs(guildId: string): Observable<VcHub[]> {
    return this.http.get<VcHub[]>(this.url(guildId, 'vc-hubs'));
  }
  createHub(guildId: string, hub: VcHub): Observable<VcHub> {
    return this.http.post<VcHub>(this.url(guildId, 'vc-hubs'), hub);
  }
  updateHub(guildId: string, hub: VcHub): Observable<VcHub> {
    return this.http.put<VcHub>(this.url(guildId, `vc-hubs/${hub.id}`), hub);
  }
  deleteHub(guildId: string, hubId: string): Observable<void> {
    return this.http.delete<void>(this.url(guildId, `vc-hubs/${hubId}`));
  }
  selfRolePanels(guildId: string): Observable<SelfRolePanel[]> {
    return this.http.get<SelfRolePanel[]>(
      this.url(guildId, 'self-role-panels'),
    );
  }
  selfRoleResources(guildId: string): Observable<SelfRoleResources> {
    const cached = this.selfRoleResourcesCache.get(guildId);
    if (cached && cached.expiresAt > Date.now()) return of(cached.value);

    const inFlight = this.selfRoleResourcesRequests.get(guildId);
    if (inFlight) return inFlight;

    const request = this.http.get<SelfRoleResources>(this.url(guildId, 'self-role-resources')).pipe(
      tap(value => this.selfRoleResourcesCache.set(guildId, { value, expiresAt: Date.now() + this.RESOURCE_CACHE_MS })),
      finalize(() => this.selfRoleResourcesRequests.delete(guildId)),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    this.selfRoleResourcesRequests.set(guildId, request);
    return request;
  }
  cancelScheduledSeasons(guildId: string): Observable<SeasonBulkOperationResult> {
    return this.http.post<SeasonBulkOperationResult>(
      this.url(guildId, 'xp/seasons/cancel-scheduled'),
      {},
    );
  }
  deleteCancelledSeasons(guildId: string): Observable<SeasonBulkOperationResult> {
    return this.http.delete<SeasonBulkOperationResult>(
      this.url(guildId, 'xp/seasons/cancelled'),
    );
  }
  invalidateResourceCache(guildId?: string): void {
    if (guildId) {
      this.resourcesCache.delete(guildId);
      this.selfRoleResourcesCache.delete(guildId);
      return;
    }
    this.resourcesCache.clear();
    this.selfRoleResourcesCache.clear();
  }
  createSelfRolePanel(
    guildId: string,
    panel: SelfRolePanel,
  ): Observable<SelfRolePanel> {
    return this.http.post<SelfRolePanel>(
      this.url(guildId, 'self-role-panels'),
      panel,
    );
  }
  updateSelfRolePanel(
    guildId: string,
    panel: SelfRolePanel,
  ): Observable<SelfRolePanel> {
    return this.http.put<SelfRolePanel>(
      this.url(guildId, `self-role-panels/${panel.id}`),
      panel,
    );
  }
  deleteSelfRolePanel(guildId: string, panelId: string): Observable<void> {
    return this.http.delete<void>(
      this.url(guildId, `self-role-panels/${panelId}`),
    );
  }
  scanPermissionDiagnostics(
    guildId: string,
    scope: PermissionDiagnosticScope = 'ConfiguredFeatures',
    includePermissionTrace = true,
  ): Observable<PermissionDiagnosticReport> {
    return this.http.post<PermissionDiagnosticReport>(
      this.url(guildId, 'diagnostics/permissions/scan'),
      { scope, includePermissionTrace },
    );
  }
  latestPermissionDiagnostics(
    guildId: string,
  ): Observable<PermissionDiagnosticReport> {
    return this.http.get<PermissionDiagnosticReport>(
      this.url(guildId, 'diagnostics/permissions/latest'),
    );
  }
  channelPermissionDiagnostics(
    guildId: string,
    channelId: string,
  ): Observable<ChannelDiagnostic> {
    return this.http.get<ChannelDiagnostic>(
      this.url(guildId, `diagnostics/permissions/channels/${channelId}`),
    );
  }
}
