using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Rankoon.Data.Model;

public sealed class GuildRolePermissionPolicy
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("role_grants")] public List<GuildRoleModuleGrant> RoleGrants { get; set; } = [];
    [BsonElement("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class GuildRoleModuleGrant
{
    [BsonElement("role_id")] public ulong RoleId { get; set; }
    [BsonElement("module_ids")] public List<string> ModuleIds { get; set; } = [];
}
