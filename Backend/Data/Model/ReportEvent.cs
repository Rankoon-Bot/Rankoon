using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace Rankoon.Data.Model;

public sealed class ReportEvent
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("category")] public string Category { get; set; } = string.Empty;
    [BsonElement("name")] public string Name { get; set; } = string.Empty;
    [BsonElement("group_key")] public string GroupKey { get; set; } = string.Empty;
    [BsonElement("action")] public string? Action { get; set; }
    [BsonElement("outcome")] public string Outcome { get; set; } = string.Empty;
    [BsonElement("severity")] public string? Severity { get; set; }
    [BsonElement("actor_id")] public ulong? ActorId { get; set; }
    [BsonElement("subject_id")] public ulong? SubjectId { get; set; }
    [BsonElement("channel_id")] public ulong? ChannelId { get; set; }
    [BsonElement("correlation_id")] public string? CorrelationId { get; set; }
    [BsonElement("duration_ms")] public long? DurationMs { get; set; }
    [BsonElement("metadata")] public Dictionary<string, string> Metadata { get; set; } = [];
    [BsonElement("occurred_at")] public DateTime OccurredAt { get; set; }
    [BsonElement("recorded_at")] public DateTime RecordedAt { get; set; }
    [BsonElement("expires_at")] public DateTime ExpiresAt { get; set; }
    [BsonElement("schema_version")] public int SchemaVersion { get; set; } = 1;
}
