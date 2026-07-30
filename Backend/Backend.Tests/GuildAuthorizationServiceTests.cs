using System.Security.Claims;
using Discord.WebSocket;
using Rankoon.Data.Auth;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Xunit;

namespace Backend.Tests;

public sealed class GuildAuthorizationServiceTests
{
    [Fact]
    public async Task OAuth_owner_can_access_modules_without_a_runtime_context()
    {
        const ulong userId = 42;
        const ulong guildId = 84;
        var modules = new GuildModuleRegistry();
        var authorization = new GuildAuthorizationService(
            new NullGuildResolver(),
            new OwnerGuildProvider(userId, guildId),
            new UnusedPermissionService(),
            modules);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("discord_id", userId.ToString())]));

        var accessibleModules = await authorization.GetAccessibleModuleIdsAsync(user, guildId);

        Assert.Equal(modules.Modules.Select(module => module.Id), accessibleModules);
        Assert.True(await authorization.CanAccessModuleAsync(user, guildId, GuildModuleIds.Xp));
        Assert.False(await authorization.CanAccessModuleAsync(user, guildId, "reporting"));
    }

    [Fact]
    public void Delegated_access_is_the_canonical_union_of_member_roles()
    {
        var registry = new GuildModuleRegistry();
        var policy = new GuildRolePermissionPolicy
        {
            RoleGrants =
            [
                new() { RoleId = 1, ModuleIds = [GuildModuleIds.Xp, "unknown"] },
                new() { RoleId = 2, ModuleIds = [GuildModuleIds.Analytics, GuildModuleIds.Xp] },
                new() { RoleId = 3, ModuleIds = [GuildModuleIds.Diagnostics] }
            ]
        };

        var accessible = GuildAuthorizationService.ResolveDelegatedModuleIds(policy, new HashSet<ulong> { 1, 2 }, registry.Modules);

        Assert.Equal([GuildModuleIds.Xp, GuildModuleIds.Analytics], accessible);
        Assert.DoesNotContain("unknown", accessible);
        Assert.DoesNotContain("reporting", accessible);
    }

    private sealed class NullGuildResolver : IGuildDiscordContextResolver
    {
        public ValueTask<GuildDiscordContext?> ResolveAsync(ulong guildId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<GuildDiscordContext?>(null);

        public Task<IReadOnlyDictionary<ulong, GuildDiscordContext>> ResolveManyAsync(IEnumerable<ulong> guildIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<ulong, GuildDiscordContext>>(new Dictionary<ulong, GuildDiscordContext>());
    }

    private sealed class OwnerGuildProvider(ulong ownerId, ulong ownedGuildId) : IUserDiscordGuildProvider
    {
        public Task<IReadOnlyList<DiscordGuildInfo>> GetGuildsAsync(ulong discordUserId, bool refresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DiscordGuildInfo>>([]);

        public Task<bool> IsGuildOwnerAsync(ulong discordUserId, ulong guildId, CancellationToken cancellationToken = default, bool refresh = false) =>
            Task.FromResult(discordUserId == ownerId && guildId == ownedGuildId);

        public Task<bool> IsGuildMemberAsync(ulong discordUserId, ulong guildId, CancellationToken cancellationToken = default) =>
            Task.FromResult(discordUserId == ownerId && guildId == ownedGuildId);
    }

    private sealed class UnusedPermissionService : IGuildRolePermissionService
    {
        public Task<GuildRolePermissionPolicy> GetOrInitializeAsync(SocketGuild guild, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Owner authorization must not resolve role permissions.");

        public Task<GuildRolePermissionPolicy?> ReplaceAsync(SocketGuild guild, IReadOnlyCollection<GuildRoleModuleGrant> roleGrants, long expectedRevision, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Owner authorization must not replace role permissions.");
    }
}
