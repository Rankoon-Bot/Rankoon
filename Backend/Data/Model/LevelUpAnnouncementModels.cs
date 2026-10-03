using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;
using System.Text.Json.Serialization;

namespace Rankoon.Data.Model;

public enum LevelTransitionStatus { Pending, Processing, Delivered, CompletedWithoutAnnouncement, RetryScheduled, DeadLetter }
public enum RewardRoleRequirement { Any, Required, NotAwarded }
public enum LevelProgressScope { Lifetime, Season }
public enum LevelAnnouncementKind { LevelUp, Reward }

public sealed class GuildLevelUpAnnouncementSettings
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("schema_version")] public int SchemaVersion { get; set; } = 2;
    [BsonElement("lifetime")] public LevelAnnouncementProfile Lifetime { get; set; } = new();
    [BsonElement("season")] public LevelAnnouncementProfile Season { get; set; } = new();
    [BsonElement("revision")] public long Revision { get; set; }
    [BsonElement("updated_at_utc")] public DateTime UpdatedAtUtc { get; set; }

    // These fields only exist to read schema-version 1 documents. They are never written back.
    [BsonElement("enabled"), JsonIgnore] public bool LegacyEnabled { get; set; }
    [BsonElement("channel_id"), JsonIgnore] public ulong? LegacyChannelId { get; set; }
    [BsonElement("notify_mentioned_user"), JsonIgnore] public bool LegacyNotifyMentionedUser { get; set; } = true;
    [BsonElement("use_default_fallback"), JsonIgnore] public bool LegacyUseDefaultFallback { get; set; } = true;
    [BsonElement("fallback_locale"), JsonIgnore] public string? LegacyFallbackLocale { get; set; }
    [BsonElement("announce_manual_adjustments"), JsonIgnore] public bool LegacyAnnounceManualAdjustments { get; set; }
    [BsonElement("avoid_recent_templates_per_user"), JsonIgnore] public int LegacyAvoidRecentTemplatesPerUser { get; set; } = 3;
    [BsonElement("templates"), JsonIgnore] public List<LevelUpMessageTemplate> LegacyTemplates { get; set; } = [];
}

public sealed class LevelAnnouncementProfile
{
    [BsonElement("enabled")] public bool Enabled { get; set; }
    [BsonElement("channel_id")] public ulong? ChannelId { get; set; }
    [BsonElement("notify_user")] public bool NotifyUser { get; set; } = true;
    [BsonElement("announce_manual_adjustments")] public bool AnnounceManualAdjustments { get; set; }
    [BsonElement("avoid_recent_messages_per_user")] public int AvoidRecentMessagesPerUser { get; set; } = 3;
    [BsonElement("avoid_recent_messages_per_guild")] public int AvoidRecentMessagesPerGuild { get; set; } = 10;
    [BsonElement("use_default_fallback")] public bool UseDefaultFallback { get; set; } = true;
    [BsonElement("fallback_locale")] public string FallbackLocale { get; set; } = "en";
    [BsonElement("level_up")] public LevelAnnouncementSet LevelUp { get; set; } = new();
    [BsonElement("rewards")] public LevelAnnouncementSet Rewards { get; set; } = new();
}

public sealed class LevelAnnouncementSet { [BsonElement("groups")] public List<LevelAnnouncementGroup> Groups { get; set; } = []; }
public sealed class LevelAnnouncementGroup
{
    [BsonElement("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [BsonElement("name")] public string Name { get; set; } = string.Empty;
    [BsonElement("enabled")] public bool Enabled { get; set; } = true;
    [BsonElement("weight")] public int Weight { get; set; } = 1;
    [BsonElement("conditions")] public LevelAnnouncementConditions Conditions { get; set; } = new();
    [BsonElement("messages")] public List<LevelAnnouncementMessage> Messages { get; set; } = [];
}
public sealed class LevelAnnouncementMessage
{
    [BsonElement("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [BsonElement("enabled")] public bool Enabled { get; set; } = true;
    [BsonElement("content")] public string Content { get; set; } = string.Empty;
}
public sealed class LevelAnnouncementConditions
{
    [BsonElement("minimum_level")] public int? MinimumLevel { get; set; }
    [BsonElement("maximum_level")] public int? MaximumLevel { get; set; }
    [BsonElement("every_nth_level")] public int? EveryNthLevel { get; set; }
    [BsonElement("exact_levels")] public List<int> ExactLevels { get; set; } = [];
    [BsonElement("sources")] public List<string> Sources { get; set; } = [];
}

public sealed record LevelAnnouncementRecentSelection(string? GroupId, string? MessageId);

// Retained exclusively for schema-v1 MongoDB deserialization and migration.
public sealed class LevelUpMessageTemplate
{
    [BsonElement("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [BsonElement("name")] public string Name { get; set; } = string.Empty;
    [BsonElement("content")] public string Content { get; set; } = string.Empty;
    [BsonElement("contents")] public List<string> Contents { get; set; } = [];
    [BsonElement("enabled")] public bool Enabled { get; set; } = true;
    [BsonElement("priority")] public int Priority { get; set; }
    [BsonElement("weight")] public int Weight { get; set; } = 1;
    [BsonElement("minimum_level")] public int? MinimumLevel { get; set; }
    [BsonElement("maximum_level")] public int? MaximumLevel { get; set; }
    [BsonElement("every_nth_level")] public int? EveryNthLevel { get; set; }
    [BsonElement("exact_levels")] public List<int> ExactLevels { get; set; } = [];
    [BsonElement("reward_role_requirement"), BsonRepresentation(BsonType.String)] public RewardRoleRequirement RewardRoleRequirement { get; set; }
    [BsonElement("sources")] public List<string> Sources { get; set; } = [];
    public IReadOnlyList<string> EffectiveContents => Contents.Count > 0 ? Contents : string.IsNullOrEmpty(Content) ? [] : [Content];
}

public sealed class LevelTransitionEvent
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("event_key")] public string EventKey { get; set; } = string.Empty;
    [BsonElement("ledger_grant_key"), BsonIgnoreIfNull] public string? LedgerGrantKey { get; set; }
    [BsonElement("cause_key")] public string CauseKey { get; set; } = string.Empty;
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("user_id")] public ulong UserId { get; set; }
    [BsonElement("scope"), BsonRepresentation(BsonType.String)] public LevelProgressScope Scope { get; set; } = LevelProgressScope.Lifetime;
    [BsonElement("season_id"), BsonIgnoreIfNull] public string? SeasonId { get; set; }
    [BsonElement("season_name_snapshot"), BsonIgnoreIfNull] public string? SeasonNameSnapshot { get; set; }
    [BsonElement("source")] public string Source { get; set; } = string.Empty;
    [BsonElement("source_channel_id"), BsonIgnoreIfNull] public ulong? SourceChannelId { get; set; }
    [BsonElement("gained_xp")] public decimal GainedXp { get; set; }
    [BsonElement("suppress_announcement")] public bool SuppressAnnouncement { get; set; }
    [BsonElement("previous_total_xp")] public decimal PreviousTotalXp { get; set; }
    [BsonElement("new_total_xp")] public decimal NewTotalXp { get; set; }
    [BsonElement("previous_level")] public int PreviousLevel { get; set; }
    [BsonElement("new_level")] public int NewLevel { get; set; }
    [BsonElement("status"), BsonRepresentation(BsonType.String)] public LevelTransitionStatus Status { get; set; } = LevelTransitionStatus.Pending;
    [BsonElement("delivery_attempts")] public int DeliveryAttempts { get; set; }
    [BsonElement("next_attempt_at_utc")] public DateTime? NextAttemptAtUtc { get; set; }
    [BsonElement("selected_template_id"), BsonIgnoreIfNull] public string? SelectedTemplateId { get; set; }
    [BsonElement("selected_kind"), BsonRepresentation(BsonType.String), BsonIgnoreIfNull] public LevelAnnouncementKind? SelectedKind { get; set; }
    [BsonElement("selected_group_id"), BsonIgnoreIfNull] public string? SelectedGroupId { get; set; }
    [BsonElement("selected_message_id"), BsonIgnoreIfNull] public string? SelectedMessageId { get; set; }
    [BsonElement("reward_role_ids")] public List<ulong> RewardRoleIds { get; set; } = [];
    [BsonElement("delivery_channel_id")] public ulong? DeliveryChannelId { get; set; }
    [BsonElement("discord_message_id")] public ulong? DiscordMessageId { get; set; }
    [BsonElement("lease_owner")] public string? LeaseOwner { get; set; }
    [BsonElement("lease_expires_at_utc")] public DateTime? LeaseExpiresAtUtc { get; set; }
    [BsonElement("last_error_code")] public string? LastErrorCode { get; set; }
    [BsonElement("created_at_utc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("completed_at_utc")] public DateTime? CompletedAtUtc { get; set; }
}

public sealed class LevelTransitionSnapshot
{
    [BsonElement("previous_total_xp")] public decimal PreviousTotalXp { get; set; }
    [BsonElement("new_total_xp")] public decimal NewTotalXp { get; set; }
    [BsonElement("previous_level")] public int PreviousLevel { get; set; }
    [BsonElement("new_level")] public int NewLevel { get; set; }
}
