using MongoDB.Bson.Serialization.Attributes;

namespace Rankoon.Data.Model;

public sealed class XpIntegritySweepCursor
{
    [BsonId] public string Id { get; set; } = string.Empty;
    [BsonElement("after_user_id")] public ulong AfterUserId { get; set; }
    [BsonElement("updated_at_utc")] public DateTime UpdatedAtUtc { get; set; }
}
