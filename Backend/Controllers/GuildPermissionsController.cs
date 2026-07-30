using Discord.WebSocket;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rankoon.Data.Auth;
using Rankoon.Data.Model;
using Rankoon.Data.Reporting;
using Rankoon.Data.Xp;
using Rankoon.Api;
using Rankoon.Data.Discord;

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
    IGuildDiscordContextResolver discord,
    LeaderboardService leaderboard,
    IReportWriter reports) : ControllerBase
{
    [HttpGet("capabilities")]
    public async Task<IActionResult> Capabilities(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id)) return this.ApiError("guild.invalidId");
        if (!await authorization.IsMemberAsync(User, id, HttpContext.RequestAborted)) return Forbid();
        var guild = (await discord.ResolveAsync(id, HttpContext.RequestAborted))?.Guild;
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
            .Where(IsDelegatableRole)
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
            if (grant.ModuleIds.Count == 0)
                return this.ApiError("permissions.emptyGrant", new Dictionary<string, object?> { ["roleId"] = grant.RoleId });
            var invalidModuleId = grant.ModuleIds.FirstOrDefault(moduleId => !modules.Contains(moduleId));
            if (invalidModuleId != null)
                return this.ApiError("permissions.unknownModule", new Dictionary<string, object?> { ["moduleId"] = invalidModuleId });
        }

        var previous = await permissions.GetOrInitializeAsync(guild, HttpContext.RequestAborted);
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
                ["oldRevision"] = previous.Revision,
                ["newRevision"] = saved.Revision,
                ["addedRoles"] = AddedRoleIds(previous.RoleGrants, saved.RoleGrants).Count(),
                ["removedRoles"] = AddedRoleIds(saved.RoleGrants, previous.RoleGrants).Count(),
                ["addedModules"] = AddedModuleAssignments(previous.RoleGrants, saved.RoleGrants).Count(),
                ["removedModules"] = AddedModuleAssignments(saved.RoleGrants, previous.RoleGrants).Count()
            }), HttpContext.RequestAborted);
        return Ok(CreateRolePermissionsResponse(guild, saved));
    }

    private object CreateRolePermissionsResponse(SocketGuild guild, GuildRolePermissionPolicy policy)
    {
        var grants = policy.RoleGrants.ToDictionary(grant => grant.RoleId, grant => grant.ModuleIds);
        var allModuleIds = modules.Modules.Select(module => module.Id).ToArray();
        var roles = guild.Roles
            .Where(IsManuallyCreatedRole)
            .OrderByDescending(role => role.Position)
            .Select(role =>
            {
                IReadOnlyList<string> moduleIds = role.Permissions.Administrator ? [] : grants.GetValueOrDefault(role.Id) ?? [];
                IReadOnlyList<string> effectiveModuleIds = role.Permissions.Administrator ? allModuleIds : moduleIds;
                var accessSource = role.Permissions.Administrator ? "DiscordAdministrator" : moduleIds.Count > 0 ? "Delegated" : "None";
                return new
                {
                    id = role.Id,
                    role.Name,
                    role.Position,
                    colorHex = $"#{role.Color.RawValue:X6}",
                    isAdministrator = role.Permissions.Administrator,
                    moduleIds,
                    effectiveModuleIds,
                    accessSource
                };
            })
            .ToArray();
        return new { guildId = guild.Id, isOwner = true, modules = modules.Modules, roles, policy.Revision, policy.UpdatedAt };
    }

    private static bool IsManuallyCreatedRole(SocketRole role) => !role.IsManaged && !role.IsEveryone;
    private static bool IsDelegatableRole(SocketRole role) => IsManuallyCreatedRole(role) && !role.Permissions.Administrator;

    internal static IEnumerable<string> AddedRoleIds(IEnumerable<GuildRoleModuleGrant> oldGrants, IEnumerable<GuildRoleModuleGrant> newGrants)
    {
        var oldRoleIds = oldGrants.Select(grant => grant.RoleId).ToHashSet();
        return newGrants.Select(grant => grant.RoleId).Where(roleId => !oldRoleIds.Contains(roleId)).Order().Select(roleId => roleId.ToString());
    }

    internal static IEnumerable<string> AddedModuleAssignments(IEnumerable<GuildRoleModuleGrant> oldGrants, IEnumerable<GuildRoleModuleGrant> newGrants)
    {
        var oldAssignments = oldGrants.SelectMany(grant => grant.ModuleIds.Select(moduleId => $"{grant.RoleId}:{moduleId}")).ToHashSet(StringComparer.Ordinal);
        return newGrants.SelectMany(grant => grant.ModuleIds.Select(moduleId => $"{grant.RoleId}:{moduleId}"))
            .Where(assignment => !oldAssignments.Contains(assignment))
            .Order(StringComparer.Ordinal);
    }

    private async Task<(SocketGuild? Guild, IActionResult? Error)> AuthorizeOwnerAsync(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id)) return (null, this.ApiError("guild.invalidId"));
        if (!await authorization.IsOwnerAsync(User, id, HttpContext.RequestAborted)) return (null, Forbid());
        var guild = (await discord.ResolveAsync(id, HttpContext.RequestAborted))?.Guild;
        return guild == null ? (null, NotFound()) : (guild, null);
    }
}
