export type XpNumber = string | number;
export type XpLedgerKind = 'AutomaticGrant' | 'AutomaticReversal' | 'ManualAdjustment' | 'ManualAdjustmentReversal' | 'SystemMigration';
export type XpLedgerScope = 'LifetimeOnly' | 'LifetimeAndSeason' | 'SeasonOnly';
export type XpLedgerDirection = 'Positive' | 'Negative';
export type XpAuditMemberSort = 'TotalXpDescending' | 'NameAscending' | 'NameDescending';

export interface XpAuditMember {
  userId: string;
  displayName: string;
  isCurrentMember: boolean;
  totalXp: XpNumber;
  level: number;
  iconUrl?: string | null;
}

export interface XpAuditMemberPage {
  items: XpAuditMember[];
  nextCursor: string | null;
}

export interface XpAuditPermissions {
  canAdjust: boolean;
  isSelf: boolean;
  isOwner: boolean;
}

export interface XpTotals {
  importedXp?: XpNumber;
  startingXp?: XpNumber;
  earnedXp: XpNumber;
  manualAdjustment: XpNumber;
  totalXp: XpNumber;
  level: number;
  rank: XpNumber;
}

export interface XpSeasonTotals extends XpTotals {
  seasonId: string;
  name: string;
}

export interface XpAuditDetails {
  userId: string;
  displayName: string;
  isCurrentMember: boolean;
  iconUrl?: string | null;
  lastXpActivityAtUtc: string | null;
  lifetime: XpTotals;
  activeSeason: XpSeasonTotals | null;
  permissions: XpAuditPermissions;
}

export interface XpLedgerEntry {
  id: string;
  grantKey: string;
  source: string;
  kind: XpLedgerKind;
  scope: XpLedgerScope;
  amount: XpNumber;
  displayName: string;
  occurredAtUtc: string;
  createdAtUtc: string;
  projectedAtUtc: string | null;
  projectionStatus: string;
  channelId: string | null;
  seasonId: string | null;
  seasonName: string | null;
  periodStartsAtUtc: string | null;
  periodEndsAtUtc: string | null;
  actorUserId: string | null;
  actorDisplayName: string | null;
  reason: string | null;
  reference: string | null;
  requestId: string | null;
  reversesGrantKey: string | null;
  reversesLedgerEntryId: string | null;
  reversedByLedgerEntryId: string | null;
}

export interface XpAuditEntryPage {
  items: XpLedgerEntry[];
  nextCursor: string | null;
}

export interface XpAuditEntryFilter {
  source?: string | null;
  kind?: XpLedgerKind | null;
  scope?: XpLedgerScope | null;
  direction?: XpLedgerDirection | null;
  from?: string | null;
  to?: string | null;
  seasonId?: string | null;
  actorUserId?: string | null;
  projectionStatus?: string | null;
  channelId?: string | null;
  cursor?: string | null;
}

export type UserXpModalTab = 'overview' | 'history' | 'adjust';

export interface UserXpModalSubject {
  guildId: string;
  userId: string;
  displayName: string;
  iconUrl?: string | null;
  isCurrentMember?: boolean;
}

export interface UserXpModalOpenOptions {
  initialTab?: UserXpModalTab;
  trigger?: Event | HTMLElement | null;
}

export interface XpTimelineItem {
  itemType: 'Entry' | 'VoiceDay';
  id: string;
  entry: XpLedgerEntry | null;
  voiceDay: XpVoiceDay | null;
  source: string;
  kind: XpLedgerKind;
  scope: XpLedgerScope;
  totalAmount: XpNumber;
  entryCount: number;
  occurredFromUtc: string;
  occurredToUtc: string;
  periodStartsAtUtc: string | null;
  periodEndsAtUtc: string | null;
  durationSeconds: number | null;
  channelId: string | null;
  seasonId: string | null;
  seasonName: string | null;
  projectionStatus: string;
  appliedServerBoosterMultiplier: XpNumber | null;
  isPartial: boolean;
  dayKey: string | null;
}
export interface XpAuditTimelinePage { items: XpTimelineItem[]; nextCursor: string | null; }

export interface XpVoiceDay {
  day: string;
  totalXp: XpNumber;
  eligibleSeconds: number;
  segmentCount: number;
  sessionCount: number;
  sessionIds: string[];
  channelCount: number;
  channelIds: string[];
  seasonIds: string[];
  activityStartsAtUtc: string;
  activityEndsAtUtc: string;
}

export interface XpVoiceSegment {
  id: string;
  startsAtUtc: string;
  endsAtUtc: string;
  durationSeconds: number;
  xp: XpNumber;
  channelId: string;
  seasonId: string | null;
  seasonName: string | null;
  effectiveXpPerMinute: XpNumber;
  channelMultiplier: XpNumber;
  serverBoosterMultiplier: XpNumber | null;
}

export interface XpVoiceSegmentPage { items: XpVoiceSegment[]; }

export interface AdjustmentRequest {
  amount: number;
  scope: 'LifetimeOnly' | 'LifetimeAndSeason';
  reason: string;
  reference?: string;
  requestId: string;
}
