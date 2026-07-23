using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rankoon.Api;
using Rankoon.Data.Auth;
using Rankoon.Data.Diagnostics;

namespace Rankoon.Controllers;

[ApiController]
[Authorize]
[Route("api/guilds/{guildId}/diagnostics/permissions")]
public sealed class PermissionDiagnosticsController(IGuildAuthorizationService authorization, IBotPermissionDiagnosticService diagnostics) : ControllerBase
{
    private async Task<(ulong Id, IActionResult? Error)> AuthorizeGuildAsync(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id)) return (0, this.ApiError("guild.invalidId"));
        if (!await authorization.CanAccessModuleAsync(User, id, GuildModuleIds.Diagnostics, HttpContext.RequestAborted)) return (0, Forbid());
        return (id, null);
    }

    [HttpPost("scan")]
    public async Task<IActionResult> Scan(string guildId, [FromBody] PermissionDiagnosticScanRequest? request)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId); if (error != null) return error;
        return Ok(await diagnostics.ScanAsync(id, request ?? new(), HttpContext.RequestAborted));
    }

    [HttpGet("latest")]
    public async Task<IActionResult> Latest(string guildId)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId); if (error != null) return error;
        var report = diagnostics.GetLatest(id);
        return report == null ? NotFound() : Ok(report);
    }

    [HttpGet("channels/{channelId}")]
    public async Task<IActionResult> Channel(string guildId, string channelId, [FromQuery] bool includePermissionTrace = true)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId); if (error != null) return error;
        if (!ulong.TryParse(channelId, out var resourceId)) return this.ApiError("channel.invalidId");
        var result = await diagnostics.GetChannelAsync(id, resourceId, includePermissionTrace, HttpContext.RequestAborted);
        return result == null ? NotFound() : Ok(result);
    }
}
