using System.Net;
using System.Security.Claims;
using Discord;
using Discord.WebSocket;

namespace Rankoon.Data.Auth;

public interface IGuildAuthorizationService
{
    Task<IGuildUser?> ResolveMemberAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default);
    Task<bool> IsOwnerAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default);
    Task<bool> IsMemberAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default);
    Task<bool> CanAccessAnyModuleAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default);
    Task<bool> CanAccessModuleAsync(ClaimsPrincipal user, ulong guildId, string moduleId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetAccessibleModuleIdsAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default);
    ulong? GetDiscordUserId(ClaimsPrincipal user);
}

public sealed class GuildAuthorizationService(
    DiscordShardedClient discord,
    IGuildRolePermissionService permissions,
    IGuildModuleRegistry modules) : IGuildAuthorizationService
{
    public async Task<IGuildUser?> ResolveMemberAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default)
    {
        var userId = GetDiscordUserId(user);
        var guild = discord.GetGuild(guildId);
        if (userId == null || guild == null) return null;

        IGuildUser? member = guild.GetUser(userId.Value);
        try
        {
            return member ?? await discord.Rest.GetGuildUserAsync(guildId, userId.Value, new RequestOptions { CancelToken = cancellationToken });
        }
        catch (global::Discord.Net.HttpException exception) when (exception.HttpCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return null;
        }
    }

    public Task<bool> IsOwnerAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default)
    {
        var guild = discord.GetGuild(guildId);
        var userId = GetDiscordUserId(user);
        return Task.FromResult(guild != null && userId != null && guild.OwnerId == userId.Value);
    }

    public async Task<bool> IsMemberAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default)
    {
        var guild = discord.GetGuild(guildId);
        var userId = GetDiscordUserId(user);
        if (guild == null || userId == null) return false;
        return guild.OwnerId == userId.Value || await ResolveMemberAsync(user, guildId, cancellationToken) != null;
    }

    public async Task<bool> CanAccessAnyModuleAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default) =>
        (await GetAccessibleModuleIdsAsync(user, guildId, cancellationToken)).Count > 0;

    public async Task<bool> CanAccessModuleAsync(ClaimsPrincipal user, ulong guildId, string moduleId, CancellationToken cancellationToken = default) =>
        modules.Contains(moduleId) && (await GetAccessibleModuleIdsAsync(user, guildId, cancellationToken)).Contains(moduleId);

    public async Task<IReadOnlyList<string>> GetAccessibleModuleIdsAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default)
    {
        var guild = discord.GetGuild(guildId);
        var userId = GetDiscordUserId(user);
        if (guild == null || userId == null) return [];
        if (guild.OwnerId == userId.Value) return modules.Modules.Select(module => module.Id).ToArray();

        var member = await ResolveMemberAsync(user, guildId, cancellationToken);
        if (member == null) return [];

        var policy = await permissions.GetOrInitializeAsync(guild, cancellationToken);
        var roleIds = guild.Roles
            .Where(role => !role.IsManaged && !role.IsEveryone && member.RoleIds.Contains(role.Id))
            .Select(role => role.Id)
            .ToHashSet();
        var grantedIds = policy.RoleGrants
            .Where(grant => roleIds.Contains(grant.RoleId))
            .SelectMany(grant => grant.ModuleIds)
            .ToHashSet(StringComparer.Ordinal);
        return modules.Modules.Where(module => grantedIds.Contains(module.Id)).Select(module => module.Id).ToArray();
    }

    public ulong? GetDiscordUserId(ClaimsPrincipal user) =>
        ulong.TryParse(user.FindFirstValue("discord_id"), out var userId) ? userId : null;
}
