using MongoDB.Bson.Serialization.Attributes;

namespace Rankoon.Data.Model;

public enum VoiceLedgerMigrationPhase
{
    // Legacy ledger remains authoritative while copying or parity is unresolved.
    Copying,
    Verifying,
    // Successful parity makes compressed voice authoritative before optional deletion.
    AwaitingDeletionApproval,
    Deleting,
    Completed,
    ParityFailed
}

public sealed class VoiceLedgerMigrationState
{
    public const string SingletonId = "voice-ledger-v1";
    public const int CurrentSchemaVersion = 1;

    [BsonId] public string Id { get; set; } = SingletonId;
    [BsonElement("schema_version")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [BsonElement("phase"), BsonRepresentation(MongoDB.Bson.BsonType.String)] public VoiceLedgerMigrationPhase Phase { get; set; } = VoiceLedgerMigrationPhase.Copying;
    [BsonElement("source_high_watermark_id")] public string? SourceHighWatermarkId { get; set; }
    [BsonElement("copy_cursor_id")] public string? CopyCursorId { get; set; }
    [BsonElement("deletion_cursor_id")] public string? DeletionCursorId { get; set; }
    [BsonElement("source_documents")] public long SourceDocuments { get; set; }
    [BsonElement("source_day_parts")] public long SourceDayParts { get; set; }
    [BsonElement("source_seconds")] public long SourceSeconds { get; set; }
    [BsonElement("source_xp")] public decimal SourceXp { get; set; }
    [BsonElement("copied_documents")] public long CopiedDocuments { get; set; }
    [BsonElement("generated_day_documents")] public long GeneratedDayDocuments { get; set; }
    [BsonElement("generated_segments")] public long GeneratedSegments { get; set; }
    [BsonElement("skipped_documents")] public long SkippedDocuments { get; set; }
    [BsonElement("deleted_documents")] public long DeletedDocuments { get; set; }
    [BsonElement("parity")] public VoiceLedgerMigrationParityReport? Parity { get; set; }
    [BsonElement("deletion_approved_at_utc")] public DateTime? DeletionApprovedAtUtc { get; set; }
    [BsonElement("deletion_approval_fingerprint")] public string? DeletionApprovalFingerprint { get; set; }
    [BsonElement("failure_count")] public int FailureCount { get; set; }
    [BsonElement("last_error")] public string? LastError { get; set; }
    [BsonElement("last_error_at_utc")] public DateTime? LastErrorAtUtc { get; set; }
    [BsonElement("started_at_utc")] public DateTime StartedAtUtc { get; set; }
    [BsonElement("updated_at_utc")] public DateTime UpdatedAtUtc { get; set; }
    [BsonElement("completed_at_utc")] public DateTime? CompletedAtUtc { get; set; }
    [BsonElement("migration_duration_milliseconds")] public long MigrationDurationMilliseconds { get; set; }
}

public sealed class VoiceLedgerMigrationParityReport
{
    [BsonElement("matches")] public bool Matches { get; set; }
    [BsonElement("grouped_parity_verified")] public bool GroupedParityVerified { get; set; }
    [BsonElement("source_documents")] public long SourceDocuments { get; set; }
    [BsonElement("source_day_parts")] public long SourceDayParts { get; set; }
    [BsonElement("source_seconds")] public long SourceSeconds { get; set; }
    [BsonElement("source_xp")] public decimal SourceXp { get; set; }
    [BsonElement("target_day_parts")] public long TargetDayParts { get; set; }
    [BsonElement("target_seconds")] public long TargetSeconds { get; set; }
    [BsonElement("target_xp")] public decimal TargetXp { get; set; }
    [BsonElement("generated_day_documents")] public long GeneratedDayDocuments { get; set; }
    [BsonElement("generated_segments")] public long GeneratedSegments { get; set; }
    [BsonElement("migration_duration_milliseconds")] public long MigrationDurationMilliseconds { get; set; }
    [BsonElement("source_season_totals")] public List<VoiceSeasonTotal> SourceSeasonTotals { get; set; } = [];
    [BsonElement("target_season_totals")] public List<VoiceSeasonTotal> TargetSeasonTotals { get; set; } = [];
    [BsonElement("source_member_totals")] public List<VoiceLedgerMigrationMemberTotal> SourceMemberTotals { get; set; } = [];
    [BsonElement("target_member_totals")] public List<VoiceLedgerMigrationMemberTotal> TargetMemberTotals { get; set; } = [];
    [BsonElement("source_member_season_totals")] public List<VoiceLedgerMigrationMemberSeasonTotal> SourceMemberSeasonTotals { get; set; } = [];
    [BsonElement("target_member_season_totals")] public List<VoiceLedgerMigrationMemberSeasonTotal> TargetMemberSeasonTotals { get; set; } = [];
    [BsonElement("fingerprint")] public string Fingerprint { get; set; } = string.Empty;
    [BsonElement("checked_at_utc")] public DateTime CheckedAtUtc { get; set; }
}

public sealed class VoiceLedgerMigrationMemberTotal
{
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("user_id")] public ulong UserId { get; set; }
    [BsonElement("eligible_seconds")] public long EligibleSeconds { get; set; }
    [BsonElement("awarded_xp")] public decimal AwardedXp { get; set; }
}

public sealed class VoiceLedgerMigrationMemberSeasonTotal
{
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("user_id")] public ulong UserId { get; set; }
    [BsonElement("season_id"), BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)] public string SeasonId { get; set; } = string.Empty;
    [BsonElement("eligible_seconds")] public long EligibleSeconds { get; set; }
    [BsonElement("awarded_xp")] public decimal AwardedXp { get; set; }
}

public sealed class VoiceLedgerMigrationOptions
{
    public const string SectionName = "VoiceLedgerMigration";
    public int? CopyBatchSize { get; set; }
    public int DeleteBatchSize { get; set; } = 50;
    public int MaxRetries { get; set; } = 8;
    public int RetryDelaySeconds { get; set; } = 30;
    public int IdleDelaySeconds { get; set; } = 60;
}
