using Discord.WebSocket;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rankoon.Data.Auth;
using Rankoon.Data.Model;
using Rankoon.Data.Reporting;
using Rankoon.Data.Xp;
using Rankoon.Api;

namespace Rankoon.Controllers;

public sealed record RoleModuleGrantRequest(ulong RoleId, List<string>? ModuleIds);
public sealed record RolePermissionsRequest(long Revision, List<RoleModuleGrantRequest?>? Roles);

[ApiController]
[Authorize]
[Route("api/guilds/{guildId}")]
public sealed class GuildPermissionsController(
    IGuildAuthorizationService authorization,
    IGuildRolePermissionService permissions,
    IGuildModuleRegistry modules,
    DiscordShardedClient discord,
    LeaderboardService leaderboard,
    IReportWriter reports) : ControllerBase
{
    [HttpGet("capabilities")]
    public async Task<IActionResult> Capabilities(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id)) return this.ApiError("guild.invalidId");
        if (!await authorization.IsMemberAsync(User, id, HttpContext.RequestAborted)) return Forbid();
        var guild = discord.GetGuild(id);
        if (guild == null) return NotFound();

        var moduleIds = await authorization.GetAccessibleModuleIdsAsync(User, id, HttpContext.RequestAborted);
        var settings = await leaderboard.GetOrCreateSettingsAsync(id, guild.Name, HttpContext.RequestAborted);
        var isOwner = guild.OwnerId == authorization.GetDiscordUserId(User);
        return Ok(new { guildId = id, isOwner, canAccessSettings = moduleIds.Count > 0, moduleIds, leaderboardAlias = settings.Alias });
    }

    [HttpGet("role-permissions")]
    public async Task<IActionResult> GetRolePermissions(string guildId)
    {
        var (guild, error) = await AuthorizeOwnerAsync(guildId);
        if (error != null) return error;
        var policy = await permissions.GetOrInitializeAsync(guild!, HttpContext.RequestAborted);
        return Ok(CreateRolePermissionsResponse(guild!, policy));
    }

    [HttpPut("role-permissions")]
    public async Task<IActionResult> SaveRolePermissions(string guildId, [FromBody] RolePermissionsRequest request)
    {
        var (guild, error) = await AuthorizeOwnerAsync(guildId);
        if (error != null) return error;
        if (request.Roles == null) return this.ApiError("permissions.rolesRequired");
        if (request.Roles.Any(grant => grant == null)) return this.ApiError("permissions.nullRole");

        var selectableRoles = guild!.Roles
            .Where(IsManuallyCreatedRole)
            .ToDictionary(role => role.Id);
        var requestedRoles = request.Roles.Select(grant => grant!).ToArray();
        if (requestedRoles.Select(grant => grant.RoleId).Distinct().Count() != requestedRoles.Length)
            return this.ApiError("permissions.duplicateRole");

        foreach (var grant in requestedRoles)
        {
            if (!selectableRoles.ContainsKey(grant.RoleId))
                return this.ApiError("permissions.roleNotInGuild", new Dictionary<string, object?> { ["roleId"] = grant.RoleId });
            if (grant.ModuleIds == null)
                return this.ApiError("permissions.modulesRequired", new Dictionary<string, object?> { ["roleId"] = grant.RoleId });
            if (grant.ModuleIds.Distinct(StringComparer.Ordinal).Count() != grant.ModuleIds.Count)
                return this.ApiError("permissions.duplicateModule", new Dictionary<string, object?> { ["roleId"] = grant.RoleId });
            var invalidModuleId = grant.ModuleIds.FirstOrDefault(moduleId => !modules.Contains(moduleId));
            if (invalidModuleId != null)
                return this.ApiError("permissions.unknownModule", new Dictionary<string, object?> { ["moduleId"] = invalidModuleId });
        }

        var saved = await permissions.ReplaceAsync(guild, requestedRoles.Select(grant => new GuildRoleModuleGrant
        {
            RoleId = grant.RoleId,
            ModuleIds = [.. grant.ModuleIds!]
        }).ToArray(), request.Revision, HttpContext.RequestAborted);
        if (saved == null) return this.ApiError("permissions.revisionConflict");
        await reports.WriteAsync(new(
            guild.Id,
            ReportCategories.Activity,
            ReportNames.RolePermissionsChanged,
            ReportOutcomes.Succeeded,
            ActorId: authorization.GetDiscordUserId(User),
            Metadata: new Dictionary<string, object?>
            {
                ["configuredRoles"] = requestedRoles.Count(grant => grant.ModuleIds!.Count > 0),
                ["moduleAssignments"] = requestedRoles.Sum(grant => grant.ModuleIds!.Count)
            }), HttpContext.RequestAborted);
        return Ok(CreateRolePermissionsResponse(guild, saved));
    }

    private object CreateRolePermissionsResponse(SocketGuild guild, GuildRolePermissionPolicy policy)
    {
        var grants = policy.RoleGrants.ToDictionary(grant => grant.RoleId, grant => grant.ModuleIds);
        var roles = guild.Roles
            .Where(IsManuallyCreatedRole)
            .OrderByDescending(role => role.Position)
            .Select(role => new
            {
                id = role.Id,
                role.Name,
                role.Position,
                isAdministrator = role.Permissions.Administrator,
                moduleIds = grants.GetValueOrDefault(role.Id) ?? []
            })
            .ToArray();
        return new { guildId = guild.Id, isOwner = true, modules = modules.Modules, roles, policy.Revision, policy.UpdatedAt };
    }

    private static bool IsManuallyCreatedRole(SocketRole role) => !role.IsManaged && !role.IsEveryone;

    private async Task<(SocketGuild? Guild, IActionResult? Error)> AuthorizeOwnerAsync(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id)) return (null, this.ApiError("guild.invalidId"));
        if (!await authorization.IsOwnerAsync(User, id, HttpContext.RequestAborted)) return (null, Forbid());
        var guild = discord.GetGuild(id);
        return guild == null ? (null, NotFound()) : (guild, null);
    }
}
