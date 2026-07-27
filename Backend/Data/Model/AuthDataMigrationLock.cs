using MongoDB.Bson.Serialization.Attributes;

namespace Rankoon.Data.Model;

public sealed class AuthDataMigrationLock
{
    public const string SingletonId = "auth-data-v1";

    [BsonId] public string Id { get; set; } = SingletonId;
    [BsonElement("owner")] public string? Owner { get; set; }
    [BsonElement("expires_at_utc")] public DateTime ExpiresAtUtc { get; set; }
    [BsonElement("status")] public string Status { get; set; } = "Pending";
    [BsonElement("updated_at_utc")] public DateTime UpdatedAtUtc { get; set; }
    [BsonElement("completed_at_utc")] public DateTime? CompletedAtUtc { get; set; }
}
