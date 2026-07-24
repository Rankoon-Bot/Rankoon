using System.Net;
using System.Security.Claims;
using Discord;
using Rankoon.Data.Discord;

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
    IGuildDiscordContextResolver guildResolver,
    IUserDiscordGuildProvider userGuilds,
    IGuildRolePermissionService permissions,
    IGuildModuleRegistry modules) : IGuildAuthorizationService
{
    public async Task<IGuildUser?> ResolveMemberAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default)
    {
        var userId = GetDiscordUserId(user);
        var context = await guildResolver.ResolveAsync(guildId, cancellationToken);
        if (userId == null || context == null) return null;

        IGuildUser? member = context.Guild.GetUser(userId.Value);
        try
        {
            return member ?? await context.Client.Rest.GetGuildUserAsync(guildId, userId.Value, new RequestOptions { CancelToken = cancellationToken });
        }
        catch (global::Discord.Net.HttpException exception) when (exception.HttpCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return null;
        }
    }

    public async Task<bool> IsOwnerAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default)
    {
        var context = await guildResolver.ResolveAsync(guildId, cancellationToken);
        var userId = GetDiscordUserId(user);
        if (userId == null) return false;
        return context != null ? context.Guild.OwnerId == userId.Value : await userGuilds.IsGuildOwnerAsync(userId.Value, guildId, cancellationToken);
    }

    public async Task<bool> IsMemberAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default)
    {
        var context = await guildResolver.ResolveAsync(guildId, cancellationToken);
        var userId = GetDiscordUserId(user);
        if (userId == null) return false;
        if (context == null) return await userGuilds.IsGuildMemberAsync(userId.Value, guildId, cancellationToken);
        return context.Guild.OwnerId == userId.Value || await ResolveMemberAsync(user, guildId, cancellationToken) != null;
    }

    public async Task<bool> CanAccessAnyModuleAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default) =>
        (await GetAccessibleModuleIdsAsync(user, guildId, cancellationToken)).Count > 0;

    public async Task<bool> CanAccessModuleAsync(ClaimsPrincipal user, ulong guildId, string moduleId, CancellationToken cancellationToken = default) =>
        modules.Contains(moduleId) && (await GetAccessibleModuleIdsAsync(user, guildId, cancellationToken)).Contains(moduleId);

    public async Task<IReadOnlyList<string>> GetAccessibleModuleIdsAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default)
    {
        var context = await guildResolver.ResolveAsync(guildId, cancellationToken);
        var userId = GetDiscordUserId(user);
        if (context == null || userId == null) return [];
        var guild = context.Guild;
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
        if (grantedIds.Contains(GuildModuleIds.XpAdjustments)) grantedIds.Add(GuildModuleIds.XpAudit);
        return modules.Modules.Where(module => grantedIds.Contains(module.Id)).Select(module => module.Id).ToArray();
    }

    public ulong? GetDiscordUserId(ClaimsPrincipal user) =>
        ulong.TryParse(user.FindFirstValue("discord_id"), out var userId) ? userId : null;
}
