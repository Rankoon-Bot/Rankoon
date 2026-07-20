using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace Rankoon.Data.Model;

public sealed class GuildXpSettings
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("enabled")] public bool Enabled { get; set; } = true;
    [BsonElement("message")] public MessageXpSettings Message { get; set; } = new();
    [BsonElement("voice")] public VoiceXpSettings Voice { get; set; } = new();
    [BsonElement("reaction")] public ReactionXpSettings Reaction { get; set; } = new();
    [BsonElement("event_interest")] public EventInterestXpSettings EventInterest { get; set; } = new();
    [BsonElement("thread")] public ThreadXpSettings Thread { get; set; } = new();
    [BsonElement("excluded_channel_ids")] public List<ulong> ExcludedChannelIds { get; set; } = [];
    [BsonElement("excluded_category_ids")] public List<ulong> ExcludedCategoryIds { get; set; } = [];
    [BsonElement("excluded_role_ids")] public List<ulong> ExcludedRoleIds { get; set; } = [];
    [BsonElement("channel_multipliers")] public List<ChannelMultiplier> ChannelMultipliers { get; set; } = [];
    [BsonElement("level_roles")] public List<LevelRole> LevelRoles { get; set; } = [];
    [BsonElement("level_up_channel_id")] public ulong? LevelUpChannelId { get; set; }
    [BsonElement("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class MessageXpSettings { public bool Enabled { get; set; } = true; public int MinimumPoints { get; set; } = 5; public int MaximumPoints { get; set; } = 50; public int MinimumCharacters { get; set; } = 1; public int MaximumCharacters { get; set; } = 500; public int CooldownSeconds { get; set; } = 60; }
[BsonIgnoreExtraElements]
public sealed class VoiceXpSettings { public bool Enabled { get; set; } = true; public decimal PointsPerMinute { get; set; } = 10; public int MinimumSessionSeconds { get; set; } = 60; public int CheckIntervalSeconds { get; set; } = 30; public bool RequireMultipleHumans { get; set; } = true; public bool ExcludeAfkChannel { get; set; } = true; }
public sealed class ReactionXpSettings { public bool Enabled { get; set; } = true; public int Points { get; set; } = 2; public int CooldownSeconds { get; set; } = 30; public bool ReverseOnRemove { get; set; } = true; }
public sealed class EventInterestXpSettings { public bool Enabled { get; set; } = true; public int Points { get; set; } = 10; }
public sealed class ThreadXpSettings { public bool Enabled { get; set; } = true; public int CreatePoints { get; set; } = 15; public int MessagePoints { get; set; } = 5; public int CooldownSeconds { get; set; } = 60; }
public sealed class ChannelMultiplier { public ulong ChannelId { get; set; } public decimal Multiplier { get; set; } = 1; }
public sealed class LevelRole { public int Level { get; set; } public ulong RoleId { get; set; } }

public sealed class MemberXp
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("user_id")] public ulong UserId { get; set; }
    [BsonElement("display_name")] public string DisplayName { get; set; } = string.Empty;
    [BsonElement("imported_mee6_xp")] public long ImportedMee6Xp { get; set; }
    [BsonElement("earned_xp")] public decimal EarnedXp { get; set; }
    [BsonElement("manual_adjustment")] public long ManualAdjustment { get; set; }
    [BsonElement("total_xp")] public decimal TotalXp { get; set; }
    [BsonElement("is_current_member")] public bool IsCurrentMember { get; set; } = true;
    [BsonElement("public_leaderboard_visible")] public bool PublicLeaderboardVisible { get; set; } = true;
    [BsonElement("message_count")] public long MessageCount { get; set; }
    [BsonElement("voice_seconds")] public long VoiceSeconds { get; set; }
    [BsonElement("last_message_xp_at")] public DateTime? LastMessageXpAt { get; set; }
    [BsonElement("last_reaction_xp_at")] public DateTime? LastReactionXpAt { get; set; }
    [BsonElement("last_thread_xp_at")] public DateTime? LastThreadXpAt { get; set; }
    [BsonElement("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum LeaderboardVisibility
{
    Public,
    MembersOnly
}

public sealed class GuildLeaderboardSettings
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("alias")] public string Alias { get; set; } = string.Empty;
    [BsonElement("visibility"), BsonRepresentation(BsonType.String)] public LeaderboardVisibility Visibility { get; set; } = LeaderboardVisibility.MembersOnly;
    [BsonElement("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class MemberLeaderboardPreference
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("user_id")] public ulong UserId { get; set; }
    [BsonElement("public_visible")] public bool PublicVisible { get; set; } = true;
    [BsonElement("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class XpLedgerEntry
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("grant_key")] public string GrantKey { get; set; } = string.Empty;
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("user_id")] public ulong UserId { get; set; }
    [BsonElement("source")] public string Source { get; set; } = string.Empty;
    [BsonElement("amount")] public decimal Amount { get; set; }
    [BsonElement("channel_id")] public ulong? ChannelId { get; set; }
    [BsonElement("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class VoiceSession
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("user_id")] public ulong UserId { get; set; }
    [BsonElement("channel_id")] public ulong ChannelId { get; set; }
    [BsonElement("joined_at")] public DateTime JoinedAt { get; set; }
    [BsonElement("last_accrued_at")] public DateTime LastAccruedAt { get; set; }
    [BsonElement("eligible_seconds")] public long EligibleSeconds { get; set; }
}

public sealed class VcHub
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("join_channel_id")] public ulong JoinChannelId { get; set; }
    [BsonElement("hub_channel_name")] public string HubChannelName { get; set; } = "VC erstellen";
    [BsonElement("category_id")] public ulong? CategoryId { get; set; }
    [BsonElement("name_template")] public string NameTemplate { get; set; } = "{username}s Kanal";
    [BsonElement("user_limit")] public int UserLimit { get; set; }
    [BsonElement("bitrate")] public int Bitrate { get; set; } = 64000;
    [BsonElement("max_channels_per_owner")] public int MaxChannelsPerOwner { get; set; } = 1;
    [BsonElement("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class TemporaryVoiceChannel
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("channel_id")] public ulong ChannelId { get; set; }
    [BsonElement("hub_id")] public string HubId { get; set; } = string.Empty;
    [BsonElement("owner_id")] public ulong OwnerId { get; set; }
    [BsonElement("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class GuildStats
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("xp_awarded")] public decimal XpAwarded { get; set; }
    [BsonElement("messages")] public long Messages { get; set; }
    [BsonElement("reactions")] public long Reactions { get; set; }
    [BsonElement("threads")] public long Threads { get; set; }
    [BsonElement("event_interests")] public long EventInterests { get; set; }
    [BsonElement("temporary_channels_created")] public long TemporaryChannelsCreated { get; set; }
}
