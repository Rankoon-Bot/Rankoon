export type XpNumber = string | number;
export type XpLedgerKind = 'AutomaticGrant' | 'AutomaticReversal' | 'ManualAdjustment' | 'ManualAdjustmentReversal' | 'SystemMigration';
export type XpLedgerScope = 'LifetimeOnly' | 'LifetimeAndSeason' | 'SeasonOnly';
export interface XpAuditMember { userId: string; displayName: string; isCurrentMember: boolean; totalXp: XpNumber; level: number; }
export interface XpAuditMemberPage { items: XpAuditMember[]; nextCursor: string | null; }
export interface XpAuditDetails { userId: string; displayName: string; isCurrentMember: boolean; lastXpActivityAtUtc: string | null; lifetime: XpTotals; activeSeason: XpSeasonTotals | null; permissions: { canAdjust: boolean; isSelf: boolean; isOwner: boolean }; }
export interface XpTotals { importedXp?: XpNumber; startingXp?: XpNumber; earnedXp: XpNumber; manualAdjustment: XpNumber; totalXp: XpNumber; level: number; rank: XpNumber; }
export interface XpSeasonTotals extends XpTotals { seasonId: string; name: string; }
export interface XpLedgerEntry { id: string; grantKey: string; source: string; kind: XpLedgerKind; scope: XpLedgerScope; amount: XpNumber; occurredAtUtc: string; projectionStatus: string; channelId: string | null; seasonName: string | null; actorDisplayName: string | null; reason: string | null; reference: string | null; reversedByLedgerEntryId: string | null; }
export interface XpAuditEntryPage { items: XpLedgerEntry[]; nextCursor: string | null; }
export interface AdjustmentRequest { amount: number; scope: 'LifetimeOnly' | 'LifetimeAndSeason'; reason: string; reference?: string; requestId: string; }
