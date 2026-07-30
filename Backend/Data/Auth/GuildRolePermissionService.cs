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
    public const string Analytics = "analytics";
    public const string SelfRoles = "self-roles";
    public const string XpAudit = "xp-audit";
    public const string XpAdjustments = "xp-adjustments";
    public const string XpAnnouncements = "xp-announcements";
    public const string Diagnostics = "diagnostics";
}

public static class GuildModuleCategories
{
    public const string Progression = "Progression";
    public const string XpModeration = "XpModeration";
    public const string Community = "Community";
    public const string Insights = "Insights";
}

public static class GuildModuleImpacts
{
    public const string ReadOnly = "ReadOnly";
    public const string Configuration = "Configuration";
    public const string Sensitive = "Sensitive";
}

public sealed record GuildModuleDescriptor(string Id, string Category, string Impact, IReadOnlyList<string> RequiredModuleIds);

public interface IGuildModuleRegistry
{
    IReadOnlyList<GuildModuleDescriptor> Modules { get; }
    bool Contains(string moduleId);
}

public sealed class GuildModuleRegistry : IGuildModuleRegistry
{
    public IReadOnlyList<GuildModuleDescriptor> Modules { get; } =
    [
        new(GuildModuleIds.Xp, GuildModuleCategories.Progression, GuildModuleImpacts.Sensitive, []),
        new(GuildModuleIds.Leaderboard, GuildModuleCategories.Progression, GuildModuleImpacts.Configuration, []),
        new(GuildModuleIds.VoiceHubs, GuildModuleCategories.Community, GuildModuleImpacts.Configuration, []),
        new(GuildModuleIds.Analytics, GuildModuleCategories.Insights, GuildModuleImpacts.ReadOnly, []),
        new(GuildModuleIds.SelfRoles, GuildModuleCategories.Community, GuildModuleImpacts.Sensitive, []),
        new(GuildModuleIds.XpAudit, GuildModuleCategories.XpModeration, GuildModuleImpacts.ReadOnly, []),
        new(GuildModuleIds.XpAdjustments, GuildModuleCategories.XpModeration, GuildModuleImpacts.Sensitive, [GuildModuleIds.XpAudit]),
        new(GuildModuleIds.XpAnnouncements, GuildModuleCategories.Progression, GuildModuleImpacts.Configuration, []),
        new(GuildModuleIds.Diagnostics, GuildModuleCategories.Insights, GuildModuleImpacts.ReadOnly, [])
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
    private const string LegacyReportingModuleId = "reporting";

    public async Task<GuildRolePermissionPolicy> GetOrInitializeAsync(SocketGuild guild, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var update = Builders<GuildRolePermissionPolicy>.Update
            .SetOnInsert(policy => policy.RoleGrants, [])
            .SetOnInsert(policy => policy.Revision, 1)
            .SetOnInsert(policy => policy.UpdatedAt, now);

        GuildRolePermissionPolicy policy;
        try
        {
            policy = await database.GuildRolePermissionPolicies.FindOneAndUpdateAsync(
                policy => policy.GuildId == guild.Id,
                update,
                new FindOneAndUpdateOptions<GuildRolePermissionPolicy> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
                cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            policy = await database.GuildRolePermissionPolicies.Find(policy => policy.GuildId == guild.Id).FirstAsync(cancellationToken);
        }

        return await NormalizePersistedPolicyAsync(guild, policy, cancellationToken);
    }

    public async Task<GuildRolePermissionPolicy?> ReplaceAsync(SocketGuild guild, IReadOnlyCollection<GuildRoleModuleGrant> roleGrants, long expectedRevision, CancellationToken cancellationToken = default)
    {
        await GetOrInitializeAsync(guild, cancellationToken);
        var normalized = Normalize(roleGrants, DelegatableRoleIds(guild));
        var update = Builders<GuildRolePermissionPolicy>.Update
            .Set(policy => policy.RoleGrants, normalized)
            .Inc(policy => policy.Revision, 1)
            .Set(policy => policy.UpdatedAt, DateTime.UtcNow);
        return await database.GuildRolePermissionPolicies.FindOneAndUpdateAsync(
            policy => policy.GuildId == guild.Id && policy.Revision == expectedRevision,
            update,
            new FindOneAndUpdateOptions<GuildRolePermissionPolicy> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
    }

    private async Task<GuildRolePermissionPolicy> NormalizePersistedPolicyAsync(SocketGuild guild, GuildRolePermissionPolicy policy, CancellationToken cancellationToken)
    {
        var roleIds = DelegatableRoleIds(guild);
        while (true)
        {
            var normalized = Normalize(policy.RoleGrants, roleIds);
            if (Equivalent(policy.RoleGrants, normalized)) return policy;

            var update = Builders<GuildRolePermissionPolicy>.Update
                .Set(item => item.RoleGrants, normalized)
                .Inc(item => item.Revision, 1)
                .Set(item => item.UpdatedAt, DateTime.UtcNow);
            var migrated = await database.GuildRolePermissionPolicies.FindOneAndUpdateAsync(
                item => item.GuildId == guild.Id && item.Revision == policy.Revision,
                update,
                new FindOneAndUpdateOptions<GuildRolePermissionPolicy> { ReturnDocument = ReturnDocument.After },
                cancellationToken);
            if (migrated != null) return migrated;
            policy = await database.GuildRolePermissionPolicies.Find(item => item.GuildId == guild.Id).FirstAsync(cancellationToken);
        }
    }

    internal List<GuildRoleModuleGrant> Normalize(IEnumerable<GuildRoleModuleGrant> grants, IReadOnlySet<ulong> delegatableRoleIds)
    {
        var order = modules.Modules.Select((module, index) => (module.Id, index)).ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        var requirements = modules.Modules.ToDictionary(module => module.Id, module => module.RequiredModuleIds, StringComparer.Ordinal);
        return grants
            .Where(grant => delegatableRoleIds.Contains(grant.RoleId))
            .GroupBy(grant => grant.RoleId)
            .Select(group =>
            {
                var moduleIds = group.SelectMany(grant => grant.ModuleIds ?? [])
                    .Select(moduleId => moduleId == LegacyReportingModuleId ? GuildModuleIds.Analytics : moduleId)
                    .Where(order.ContainsKey)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var moduleId in moduleIds.ToArray())
                    foreach (var requiredModuleId in requirements[moduleId])
                        moduleIds.Add(requiredModuleId);
                return new GuildRoleModuleGrant
                {
                    RoleId = group.Key,
                    ModuleIds = [.. moduleIds.OrderBy(moduleId => order[moduleId])]
                };
            })
            .Where(grant => grant.ModuleIds.Count > 0)
            .OrderBy(grant => grant.RoleId)
            .ToList();
    }

    private static HashSet<ulong> DelegatableRoleIds(SocketGuild guild) => guild.Roles
        .Where(role => !role.IsManaged && !role.IsEveryone && !role.Permissions.Administrator)
        .Select(role => role.Id)
        .ToHashSet();

    private static bool Equivalent(IReadOnlyList<GuildRoleModuleGrant> left, IReadOnlyList<GuildRoleModuleGrant> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.RoleId == pair.Second.RoleId && pair.First.ModuleIds.SequenceEqual(pair.Second.ModuleIds, StringComparer.Ordinal));
}
