using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace Rankoon.Data.Model;

public enum SeasonScheduleKind { Manual, FixedDuration, Monthly, Quarterly, SemiAnnual, Annual }
public enum SeasonInitialXpMode { Zero, Lifetime, LifetimePercentage }
public enum SeasonCarryOverMode { None, Percentage }
public enum SeasonStatus { Scheduled, Active, Closing, Closed, Cancelled }
public enum SeasonLeaderboardScope { Lifetime, CurrentSeason, Season }
public enum SeasonLevelRoleRetention { RemoveAtSeasonEnd, Keep }
public enum SeasonProjectionStatus { Pending, Applied }

public sealed class GuildSeasonSettings
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("enabled")] public bool Enabled { get; set; }
    [BsonElement("default_leaderboard_scope"), BsonRepresentation(BsonType.String)] public SeasonLeaderboardScope DefaultLeaderboardScope { get; set; } = SeasonLeaderboardScope.Lifetime;
    [BsonElement("time_zone_id")] public string TimeZoneId { get; set; } = "UTC";
    [BsonElement("schedule_kind"), BsonRepresentation(BsonType.String)] public SeasonScheduleKind ScheduleKind { get; set; } = SeasonScheduleKind.Manual;
    [BsonElement("schedule_anchor_utc")] public DateTime? ScheduleAnchorUtc { get; set; }
    [BsonElement("fixed_duration_days")] public int? FixedDurationDays { get; set; }
    [BsonElement("gap_days")] public int GapDays { get; set; }
    [BsonElement("prepared_season_count")] public int PreparedSeasonCount { get; set; } = 3;
    [BsonElement("pause_behavior")] public string PauseBehavior { get; set; } = "NoSeasonXp";
    [BsonElement("public_history_count")] public int PublicHistoryCount { get; set; } = 3;
    [BsonElement("initial_xp_mode"), BsonRepresentation(BsonType.String)] public SeasonInitialXpMode InitialXpMode { get; set; }
    [BsonElement("initial_xp_percentage")] public decimal InitialXpPercentage { get; set; }
    [BsonElement("carry_over_mode"), BsonRepresentation(BsonType.String)] public SeasonCarryOverMode CarryOverMode { get; set; }
    [BsonElement("carry_over_percentage")] public decimal CarryOverPercentage { get; set; }
    [BsonElement("carry_over_maximum_xp")] public decimal? CarryOverMaximumXp { get; set; }
    [BsonElement("announcement_channel_id")] public ulong? AnnouncementChannelId { get; set; }
    [BsonElement("announcements")] public SeasonAnnouncementSettings Announcements { get; set; } = new();
    [BsonElement("winner_count")] public int WinnerCount { get; set; } = 3;
    [BsonElement("name_template")] public string NameTemplate { get; set; } = "Season {number}";
    [BsonElement("rotation")] public List<string> Rotation { get; set; } = [];
    [BsonElement("rotation_offset")] public int RotationOffset { get; set; }
    [BsonElement("season_level_roles")] public List<SeasonLevelRole> SeasonLevelRoles { get; set; } = [];
    [BsonElement("revision")] public long Revision { get; set; }
    [BsonElement("updated_at_utc")] public DateTime UpdatedAtUtc { get; set; }
}

public sealed class SeasonAnnouncementSettings
{
    [BsonElement("start_enabled")] public bool StartEnabled { get; set; }
    [BsonElement("end_enabled")] public bool EndEnabled { get; set; }
    [BsonElement("winner_enabled")] public bool WinnerEnabled { get; set; }
    [BsonElement("warning_offsets_minutes")] public List<int> WarningOffsetsMinutes { get; set; } = [];
}

public sealed class SeasonLevelRole
{
    [BsonElement("level")] public int Level { get; set; }
    [BsonElement("role_id")] public ulong RoleId { get; set; }
    [BsonElement("retention"), BsonRepresentation(BsonType.String)] public SeasonLevelRoleRetention Retention { get; set; }
}

public sealed class GuildSeason
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("sequence")] public long Sequence { get; set; }
    [BsonElement("name")] public string Name { get; set; } = string.Empty;
    [BsonElement("description")] public string? Description { get; set; }
    [BsonElement("status"), BsonRepresentation(BsonType.String)] public SeasonStatus Status { get; set; }
    // Present only while active so MongoDB can enforce one active season per guild without transactions.
    [BsonElement("active_guild_id"), BsonIgnoreIfNull] public ulong? ActiveGuildId { get; set; }
    [BsonElement("starts_at_utc")] public DateTime StartsAtUtc { get; set; }
    [BsonElement("ends_at_utc")] public DateTime EndsAtUtc { get; set; }
    [BsonElement("created_at_utc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("activated_at_utc")] public DateTime? ActivatedAtUtc { get; set; }
    [BsonElement("closed_at_utc")] public DateTime? ClosedAtUtc { get; set; }
    [BsonElement("previous_season_id"), BsonRepresentation(BsonType.ObjectId)] public string? PreviousSeasonId { get; set; }
    [BsonElement("schedule_revision")] public long ScheduleRevision { get; set; }
    [BsonElement("settings_snapshot")] public GuildSeasonSettings SettingsSnapshot { get; set; } = new();
    [BsonElement("carry_over_applied")] public bool CarryOverApplied { get; set; }
    [BsonElement("finalized")] public bool Finalized { get; set; }
    [BsonElement("requires_final_standing_refresh")] public bool RequiresFinalStandingRefresh { get; set; }
}

public sealed class SeasonMemberXp
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("season_id"), BsonRepresentation(BsonType.ObjectId)] public string SeasonId { get; set; } = string.Empty;
    [BsonElement("user_id")] public ulong UserId { get; set; }
    [BsonElement("display_name")] public string DisplayName { get; set; } = string.Empty;
    [BsonElement("starting_xp")] public decimal StartingXp { get; set; }
    [BsonElement("earned_xp")] public decimal EarnedXp { get; set; }
    [BsonElement("manual_adjustment")] public decimal ManualAdjustment { get; set; }
    [BsonElement("total_xp")] public decimal TotalXp { get; set; }
    [BsonElement("message_count")] public long MessageCount { get; set; }
    [BsonElement("voice_seconds")] public long VoiceSeconds { get; set; }
    [BsonElement("is_current_member")] public bool IsCurrentMember { get; set; } = true;
    [BsonElement("public_leaderboard_visible")] public bool PublicLeaderboardVisible { get; set; } = true;
    [BsonElement("updated_at_utc")] public DateTime UpdatedAtUtc { get; set; }
}

public sealed class SeasonFinalStanding
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("season_id"), BsonRepresentation(BsonType.ObjectId)] public string SeasonId { get; set; } = string.Empty;
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("user_id")] public ulong UserId { get; set; }
    [BsonElement("display_name")] public string DisplayName { get; set; } = string.Empty;
    [BsonElement("rank")] public long Rank { get; set; }
    [BsonElement("total_xp")] public decimal TotalXp { get; set; }
    [BsonElement("level")] public int Level { get; set; }
    [BsonElement("message_count")] public long MessageCount { get; set; }
    [BsonElement("voice_seconds")] public long VoiceSeconds { get; set; }
    [BsonElement("public_leaderboard_visible")] public bool PublicLeaderboardVisible { get; set; } = true;
    [BsonElement("finalized_at_utc")] public DateTime FinalizedAtUtc { get; set; }
}

public sealed class SeasonCoordinatorLease
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("owner_id")] public string OwnerId { get; set; } = string.Empty;
    [BsonElement("expires_at_utc")] public DateTime ExpiresAtUtc { get; set; }
}

public sealed class SeasonAnnouncementDelivery
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("delivery_key")] public string DeliveryKey { get; set; } = string.Empty;
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("season_id"), BsonRepresentation(BsonType.ObjectId)] public string SeasonId { get; set; } = string.Empty;
    [BsonElement("kind")] public string Kind { get; set; } = string.Empty;
    [BsonElement("delivered_at_utc")] public DateTime DeliveredAtUtc { get; set; }
}
