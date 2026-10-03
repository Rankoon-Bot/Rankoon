using MongoDB.Bson.Serialization.Attributes;

namespace Rankoon.Data.Model;

/// <summary>Per-guild opt-in for Rankoon application maintainers to manage settings in the web UI.</summary>
public sealed class GuildMaintainerAccess
{
    [BsonId] public ulong GuildId { get; set; }
    [BsonElement("enabled")] public bool Enabled { get; set; }
    [BsonElement("revision")] public long Revision { get; set; }
    [BsonElement("updated_by_user_id"), BsonIgnoreIfDefault] public ulong UpdatedByUserId { get; set; }
    [BsonElement("updated_at_utc"), BsonIgnoreIfDefault] public DateTime UpdatedAtUtc { get; set; }
}
