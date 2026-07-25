using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace Rankoon.Data.Model;

public enum VoiceActivityProjectionStatus
{
    Pending,
    Projecting,
    Applied
}

public sealed class VoiceActivityDay
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("user_id")] public ulong UserId { get; set; }
    [BsonElement("day_start_utc")] public DateTime DayStartUtc { get; set; }
    [BsonElement("part")] public int Part { get; set; }
    [BsonElement("revision")] public long Revision { get; set; }
    [BsonElement("segments")] public List<VoiceActivitySegment> Segments { get; set; } = [];
    [BsonElement("session_cursors")] public List<VoiceSessionCursor> SessionCursors { get; set; } = [];
    [BsonElement("eligible_seconds")] public long TotalEligibleSeconds { get; set; }
    [BsonElement("awarded_xp")] public decimal TotalAwardedXp { get; set; }
    [BsonElement("season_totals")] public List<VoiceSeasonTotal> SeasonTotals { get; set; } = [];
    [BsonElement("projection_status"), BsonRepresentation(BsonType.String)] public VoiceActivityProjectionStatus ProjectionStatus { get; set; } = VoiceActivityProjectionStatus.Pending;
    [BsonElement("projection_revision")] public long ProjectionRevision { get; set; }
    [BsonElement("projection_target_revision")] public long ProjectionTargetRevision { get; set; }
    [BsonElement("projection_target_eligible_seconds")] public long ProjectionTargetEligibleSeconds { get; set; }
    [BsonElement("projection_target_awarded_xp")] public decimal ProjectionTargetAwardedXp { get; set; }
    [BsonElement("projection_target_season_totals")] public List<VoiceSeasonTotal> ProjectionTargetSeasonTotals { get; set; } = [];
    [BsonElement("projected_revision")] public long ProjectedRevision { get; set; }
    [BsonElement("projected_eligible_seconds")] public long ProjectedEligibleSeconds { get; set; }
    [BsonElement("projected_awarded_xp")] public decimal ProjectedXp { get; set; }
    [BsonElement("projected_season_totals")] public List<VoiceSeasonTotal> ProjectedSeasonTotals { get; set; } = [];
    [BsonElement("projection_lease_owner"), BsonIgnoreIfNull] public string? ProjectionLeaseOwner { get; set; }
    [BsonElement("projection_lease_expires_at_utc"), BsonIgnoreIfNull] public DateTime? ProjectionLeaseExpiresAtUtc { get; set; }
    [BsonElement("projected_at_utc"), BsonIgnoreIfNull] public DateTime? ProjectedAtUtc { get; set; }
    [BsonElement("created_at_utc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updated_at_utc")] public DateTime UpdatedAtUtc { get; set; }
}

public sealed class VoiceActivitySegment
{
    [BsonElement("session_id")] public string SessionId { get; set; } = string.Empty;
    [BsonElement("starts_at_utc")] public DateTime StartsAtUtc { get; set; }
    [BsonElement("ends_at_utc")] public DateTime EndsAtUtc { get; set; }
    [BsonElement("channel_id")] public ulong ChannelId { get; set; }
    [BsonElement("season_id"), BsonRepresentation(BsonType.ObjectId), BsonIgnoreIfNull] public string? SeasonId { get; set; }
    [BsonElement("eligible_seconds")] public long EligibleSeconds { get; set; }
    [BsonElement("awarded_xp")] public decimal AwardedXp { get; set; }
    [BsonElement("effective_xp_per_minute")] public decimal EffectiveXpPerMinute { get; set; }
    [BsonElement("channel_multiplier")] public decimal ChannelMultiplier { get; set; }
    [BsonElement("server_booster_multiplier"), BsonIgnoreIfNull] public decimal? AppliedServerBoosterMultiplier { get; set; }
    [BsonElement("settings_revision")] public long SettingsRevision { get; set; }
}

public sealed class VoiceSessionCursor
{
    [BsonElement("session_id")] public string SessionId { get; set; } = string.Empty;
    [BsonElement("processed_through_utc")] public DateTime ProcessedThroughUtc { get; set; }
}

public sealed class VoiceSeasonTotal
{
    [BsonElement("season_id"), BsonRepresentation(BsonType.ObjectId)] public string? SeasonId { get; set; }
    [BsonElement("eligible_seconds")] public long EligibleSeconds { get; set; }
    [BsonElement("awarded_xp")] public decimal AwardedXp { get; set; }
    [BsonElement("projected_eligible_seconds")] public long ProjectedEligibleSeconds { get; set; }
    [BsonElement("projected_xp")] public decimal ProjectedXp { get; set; }
}
