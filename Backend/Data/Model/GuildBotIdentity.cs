using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Rankoon.Data.Model;

public enum BotIdentityMode { Rankoon, Custom }
public enum BotIdentityStatus { Default, Draft, AwaitingInstallation, Validating, Starting, Active, Reconnecting, MissingIntents, MissingPermissions, InvalidToken, RemovedFromGuild, Degraded, Disabled, DisabledByPolicy }

public sealed class GuildBotIdentity
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("mode"), BsonRepresentation(BsonType.String)] public BotIdentityMode Mode { get; set; } = BotIdentityMode.Rankoon;
    [BsonElement("status"), BsonRepresentation(BsonType.String)] public BotIdentityStatus Status { get; set; } = BotIdentityStatus.Default;
    [BsonElement("application_id")] public ulong? ApplicationId { get; set; }
    [BsonElement("bot_user_id")] public ulong? BotUserId { get; set; }
    [BsonElement("bot_username")] public string? BotUsername { get; set; }
    [BsonElement("bot_global_name")] public string? BotGlobalName { get; set; }
    [BsonElement("bot_avatar_hash")] public string? BotAvatarHash { get; set; }
    [BsonElement("encrypted_bot_token")] public string? EncryptedBotToken { get; set; }
    [BsonElement("token_fingerprint")] public string? TokenFingerprint { get; set; }
    [BsonElement("encryption_key_version")] public int EncryptionKeyVersion { get; set; } = 1;
    [BsonElement("command_schema_version")] public string? CommandSchemaVersion { get; set; }
    [BsonElement("last_validated_at")] public DateTime? LastValidatedAt { get; set; }
    [BsonElement("last_connected_at")] public DateTime? LastConnectedAt { get; set; }
    [BsonElement("last_ready_at")] public DateTime? LastReadyAt { get; set; }
    [BsonElement("last_error_code")] public string? LastErrorCode { get; set; }
    [BsonElement("last_error_at")] public DateTime? LastErrorAt { get; set; }
    [BsonElement("created_by_user_id")] public ulong CreatedByUserId { get; set; }
    [BsonElement("created_at")] public DateTime CreatedAt { get; set; }
    [BsonElement("updated_at")] public DateTime UpdatedAt { get; set; }
    [BsonElement("revision")] public long Revision { get; set; } = 1;
}

public sealed class CustomBotCapacityReservation
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("identity_id"), BsonRepresentation(BsonType.ObjectId)] public string IdentityId { get; set; } = string.Empty;
    [BsonElement("reserved_at_utc")] public DateTime ReservedAtUtc { get; set; }
    [BsonElement("reserved_by_user_id")] public ulong ReservedByUserId { get; set; }
}
