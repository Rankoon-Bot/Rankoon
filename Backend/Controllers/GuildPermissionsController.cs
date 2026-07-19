using Discord.WebSocket;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rankoon.Data.Auth;
using Rankoon.Data.Model;
using Rankoon.Data.Reporting;
using Rankoon.Data.Xp;

namespace Rankoon.Controllers;

public sealed record RoleModuleGrantRequest(ulong RoleId, List<string>? ModuleIds);
public sealed record RolePermissionsRequest(List<RoleModuleGrantRequest?>? Roles);

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
        if (!ulong.TryParse(guildId, out var id)) return BadRequest(new { error = "Invalid guild ID" });
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
        if (request.Roles == null) return BadRequest(new { error = "Roles are required." });
        if (request.Roles.Any(grant => grant == null)) return BadRequest(new { error = "Roles must not contain null entries." });

        var selectableRoles = guild!.Roles.Where(role => !role.IsManaged && !role.IsEveryone).ToDictionary(role => role.Id);
        var requestedRoles = request.Roles.Select(grant => grant!).ToArray();
        if (requestedRoles.Select(grant => grant.RoleId).Distinct().Count() != requestedRoles.Length)
            return BadRequest(new { error = "Duplicate role IDs are not allowed." });

        foreach (var grant in requestedRoles)
        {
            if (!selectableRoles.ContainsKey(grant.RoleId))
                return BadRequest(new { error = $"Role {grant.RoleId} is invalid, managed, or the everyone role." });
            if (grant.ModuleIds == null)
                return BadRequest(new { error = $"Module IDs are required for role {grant.RoleId}." });
            if (grant.ModuleIds.Distinct(StringComparer.Ordinal).Count() != grant.ModuleIds.Count)
                return BadRequest(new { error = $"Duplicate module IDs are not allowed for role {grant.RoleId}." });
            var invalidModuleId = grant.ModuleIds.FirstOrDefault(moduleId => !modules.Contains(moduleId));
            if (invalidModuleId != null)
                return BadRequest(new { error = $"Unknown module ID: {invalidModuleId}" });
        }

        var saved = await permissions.ReplaceAsync(guild, requestedRoles.Select(grant => new GuildRoleModuleGrant
        {
            RoleId = grant.RoleId,
            ModuleIds = [.. grant.ModuleIds!]
        }).ToArray(), HttpContext.RequestAborted);
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
            .Where(role => !role.IsManaged && !role.IsEveryone)
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
        return new { guildId = guild.Id, isOwner = true, modules = modules.Modules, roles, policy.UpdatedAt };
    }

    private async Task<(SocketGuild? Guild, IActionResult? Error)> AuthorizeOwnerAsync(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id)) return (null, BadRequest(new { error = "Invalid guild ID" }));
        if (!await authorization.IsOwnerAsync(User, id, HttpContext.RequestAborted)) return (null, Forbid());
        var guild = discord.GetGuild(id);
        return guild == null ? (null, NotFound()) : (guild, null);
    }
}
