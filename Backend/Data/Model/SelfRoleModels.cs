using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;
using System.Text.Json.Serialization;

namespace Rankoon.Data.Model;

public enum SelfRoleEmojiKind { Unicode, Custom }
public enum SelfRolePanelState { Pending, Published, Disabled, Degraded }

public sealed class SelfRolePanel
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("channel_id")] public ulong ChannelId { get; set; }
    [BsonElement("message_id"), JsonIgnore] public ulong MessageId { get; set; }
    [BsonElement("title")] public string Title { get; set; } = string.Empty;
    [BsonElement("description")] public string Description { get; set; } = string.Empty;
    [BsonElement("color")] public string Color { get; set; } = "#5865F2";
    [BsonElement("enabled")] public bool Enabled { get; set; } = true;
    [BsonElement("mappings")] public List<SelfRoleMapping> Mappings { get; set; } = [];
    [BsonElement("revision")] public long Revision { get; set; } = 1;
    [BsonElement("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [BsonElement("status")] public string? Status { get; set; }
    [BsonElement("state"), BsonRepresentation(BsonType.String)] public SelfRolePanelState State { get; set; } = SelfRolePanelState.Pending;
    [BsonElement("last_published_at")] public DateTime? LastPublishedAt { get; set; }
    [BsonElement("last_health_check_at")] public DateTime? LastHealthCheckAt { get; set; }
    [BsonElement("last_error")] public string? LastError { get; set; }
    [BsonElement("last_error_at")] public DateTime? LastErrorAt { get; set; }
}

public sealed class SelfRoleMapping
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("emoji")] public SelfRoleEmoji Emoji { get; set; } = new();
    [BsonElement("role_id")] public ulong RoleId { get; set; }
}

public sealed class SelfRoleEmoji
{
    [BsonElement("kind"), BsonRepresentation(BsonType.String)] public SelfRoleEmojiKind Kind { get; set; }
    [BsonElement("value")] public string Value { get; set; } = string.Empty;
    [BsonElement("name")] public string Name { get; set; } = string.Empty;
}

// Records only roles assigned by this feature; removal never affects manually granted roles.
public sealed class SelfRoleAssignment
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("panel_id"), BsonRepresentation(BsonType.ObjectId)] public string PanelId { get; set; } = string.Empty;
    [BsonElement("mapping_id"), BsonRepresentation(BsonType.ObjectId)] public string MappingId { get; set; } = string.Empty;
    [BsonElement("user_id")] public ulong UserId { get; set; }
    [BsonElement("role_id")] public ulong RoleId { get; set; }
    [BsonElement("assigned_at")] public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}
