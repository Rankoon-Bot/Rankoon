using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace Rankoon.Data.Model;

public enum LevelTransitionStatus { Pending, Processing, Delivered, CompletedWithoutAnnouncement, RetryScheduled, DeadLetter }
public enum RewardRoleRequirement { Any, Required, NotAwarded }

public sealed class GuildLevelUpAnnouncementSettings
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("enabled")] public bool Enabled { get; set; }
    [BsonElement("channel_id")] public ulong? ChannelId { get; set; }
    [BsonElement("notify_mentioned_user")] public bool NotifyMentionedUser { get; set; } = true;
    [BsonElement("use_default_fallback")] public bool UseDefaultFallback { get; set; } = true;
    [BsonElement("fallback_locale")] public string FallbackLocale { get; set; } = "de";
    [BsonElement("announce_manual_adjustments")] public bool AnnounceManualAdjustments { get; set; }
    [BsonElement("avoid_recent_templates_per_user")] public int AvoidRecentTemplatesPerUser { get; set; } = 3;
    [BsonElement("templates")] public List<LevelUpMessageTemplate> Templates { get; set; } = [];
    [BsonElement("revision")] public long Revision { get; set; }
    [BsonElement("updated_at_utc")] public DateTime UpdatedAtUtc { get; set; }
}

public sealed class LevelUpMessageTemplate
{
    [BsonElement("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [BsonElement("name")] public string Name { get; set; } = string.Empty;
    [BsonElement("content")] public string Content { get; set; } = string.Empty;
    // Content is retained for documents created before variants were introduced.
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
    [BsonElement("ledger_grant_key")] public string LedgerGrantKey { get; set; } = string.Empty;
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("user_id")] public ulong UserId { get; set; }
    [BsonElement("source")] public string Source { get; set; } = string.Empty;
    [BsonElement("previous_total_xp")] public decimal PreviousTotalXp { get; set; }
    [BsonElement("new_total_xp")] public decimal NewTotalXp { get; set; }
    [BsonElement("previous_level")] public int PreviousLevel { get; set; }
    [BsonElement("new_level")] public int NewLevel { get; set; }
    [BsonElement("status"), BsonRepresentation(BsonType.String)] public LevelTransitionStatus Status { get; set; } = LevelTransitionStatus.Pending;
    [BsonElement("delivery_attempts")] public int DeliveryAttempts { get; set; }
    [BsonElement("next_attempt_at_utc")] public DateTime? NextAttemptAtUtc { get; set; }
    [BsonElement("selected_template_id")] public string? SelectedTemplateId { get; set; }
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
