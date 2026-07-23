using Discord.WebSocket;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Auth;

public static class GuildModuleIds
{
    public const string Xp = "xp";
    public const string Leaderboard = "leaderboard";
    public const string VoiceHubs = "voice-hubs";
    public const string Reporting = "reporting";
    public const string SelfRoles = "self-roles";
    public const string XpAudit = "xp-audit";
    public const string XpAdjustments = "xp-adjustments";
    public const string XpAnnouncements = "xp-announcements";
}

public sealed record GuildModuleDescriptor(string Id);

public interface IGuildModuleRegistry
{
    IReadOnlyList<GuildModuleDescriptor> Modules { get; }
    bool Contains(string moduleId);
}

public sealed class GuildModuleRegistry : IGuildModuleRegistry
{
    public IReadOnlyList<GuildModuleDescriptor> Modules { get; } =
    [
        new(GuildModuleIds.Xp),
        new(GuildModuleIds.Leaderboard),
        new(GuildModuleIds.VoiceHubs),
        new(GuildModuleIds.Reporting),
        new(GuildModuleIds.SelfRoles),
        new(GuildModuleIds.XpAudit),
        new(GuildModuleIds.XpAdjustments),
        new(GuildModuleIds.XpAnnouncements)
    ];

    public bool Contains(string moduleId) => Modules.Any(module => module.Id == moduleId);
}

public interface IGuildRolePermissionService
{
    Task<GuildRolePermissionPolicy> GetOrInitializeAsync(SocketGuild guild, CancellationToken cancellationToken = default);
    Task<GuildRolePermissionPolicy?> ReplaceAsync(SocketGuild guild, IReadOnlyCollection<GuildRoleModuleGrant> roleGrants, long expectedRevision, CancellationToken cancellationToken = default);
}

public sealed class GuildRolePermissionService(RankoonDbContext database, IGuildModuleRegistry modules) : IGuildRolePermissionService
{
    public async Task<GuildRolePermissionPolicy> GetOrInitializeAsync(SocketGuild guild, CancellationToken cancellationToken = default)
    {
        var allModuleIds = modules.Modules.Select(module => module.Id).ToList();
        var initialGrants = guild.Roles
            .Where(role => !role.IsManaged && !role.IsEveryone && role.Permissions.Administrator)
            .Select(role => new GuildRoleModuleGrant { RoleId = role.Id, ModuleIds = [.. allModuleIds] })
            .ToList();
        var now = DateTime.UtcNow;
        var update = Builders<GuildRolePermissionPolicy>.Update
            .SetOnInsert(policy => policy.RoleGrants, initialGrants)
            .SetOnInsert(policy => policy.Revision, 1)
            .SetOnInsert(policy => policy.UpdatedAt, now);

        try
        {
            return await database.GuildRolePermissionPolicies.FindOneAndUpdateAsync(
                policy => policy.GuildId == guild.Id,
                update,
                new FindOneAndUpdateOptions<GuildRolePermissionPolicy> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
                cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return await database.GuildRolePermissionPolicies.Find(policy => policy.GuildId == guild.Id).FirstAsync(cancellationToken);
        }
    }

    public async Task<GuildRolePermissionPolicy?> ReplaceAsync(SocketGuild guild, IReadOnlyCollection<GuildRoleModuleGrant> roleGrants, long expectedRevision, CancellationToken cancellationToken = default)
    {
        await GetOrInitializeAsync(guild, cancellationToken);
        var update = Builders<GuildRolePermissionPolicy>.Update
            .Set(policy => policy.RoleGrants, Normalize(roleGrants).ToList())
            .Inc(policy => policy.Revision, 1)
            .Set(policy => policy.UpdatedAt, DateTime.UtcNow);
        return await database.GuildRolePermissionPolicies.FindOneAndUpdateAsync(
            policy => policy.GuildId == guild.Id && policy.Revision == expectedRevision,
            update,
            new FindOneAndUpdateOptions<GuildRolePermissionPolicy> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
    }

    private static IEnumerable<GuildRoleModuleGrant> Normalize(IEnumerable<GuildRoleModuleGrant> grants) =>
    grants.Select(grant => new GuildRoleModuleGrant
    {
        RoleId = grant.RoleId,
        ModuleIds = grant.ModuleIds.Contains(GuildModuleIds.XpAdjustments) ?
        [.. grant.ModuleIds.Append(GuildModuleIds.XpAudit).Distinct(StringComparer.Ordinal)] : grant.ModuleIds
    });
}
