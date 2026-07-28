using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace Rankoon.Data.Model;

public sealed class GuildUserAvatarCacheEntry
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("user_id")] public ulong UserId { get; set; }
    [BsonElement("avatar_id")] public string? AvatarId { get; set; }
    [BsonElement("guild_avatar_id")] public string? GuildAvatarId { get; set; }
    [BsonElement("default_avatar_index")] public byte? DefaultAvatarIndex { get; set; }
    [BsonElement("updated_at_utc")] public DateTime UpdatedAtUtc { get; set; }
    [BsonElement("last_observed_at_utc")] public DateTime LastObservedAtUtc { get; set; }
    [BsonElement("last_hydrated_at_utc")] public DateTime? LastHydratedAtUtc { get; set; }
    [BsonElement("needs_hydration")] public bool NeedsHydration { get; set; }
    [BsonElement("next_hydration_attempt_at_utc")] public DateTime? NextHydrationAttemptAtUtc { get; set; }
    [BsonElement("hydration_attempt_count")] public int HydrationAttemptCount { get; set; }
    [BsonElement("hydration_lease_owner")] public string? HydrationLeaseOwner { get; set; }
    [BsonElement("hydration_lease_until_utc")] public DateTime? HydrationLeaseUntilUtc { get; set; }
}
