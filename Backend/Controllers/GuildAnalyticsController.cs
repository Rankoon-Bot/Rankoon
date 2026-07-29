using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rankoon.Api;
using Rankoon.Data.Analytics;
using Rankoon.Data.Auth;

namespace Rankoon.Controllers;

[ApiController, Authorize, Route("api/guilds/{guildId}/analytics")]
public sealed class GuildAnalyticsController(IGuildAuthorizationService authorization, IGuildAnalyticsQueryService analytics) : ControllerBase
{
    [HttpGet("overview")] public Task<IActionResult> Overview(string guildId, [FromQuery] string? range) => Page(guildId, range, analytics.OverviewAsync);
    [HttpGet("xp")] public Task<IActionResult> Xp(string guildId, [FromQuery] string? range) => Page(guildId, range, analytics.XpAsync);
    [HttpGet("voice")] public Task<IActionResult> Voice(string guildId, [FromQuery] string? range) => Page(guildId, range, analytics.VoiceAsync);
    [HttpGet("features")] public Task<IActionResult> Features(string guildId, [FromQuery] string? range) => Page(guildId, range, analytics.FeaturesAsync);

    [HttpGet("timeline")]
    public async Task<IActionResult> Timeline(string guildId, [FromQuery] AnalyticsTimelineQuery query)
    {
        var access = await Access(guildId); if (access.Error != null) return access.Error;
        try { return Ok(await analytics.TimelineAsync(access.Id, query, HttpContext.RequestAborted)); }
        catch (ArgumentException) { return this.ApiError("reports.invalidQuery"); }
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit(string guildId, [FromQuery] AnalyticsAuditQuery query)
    {
        var access = await Access(guildId); if (access.Error != null) return access.Error;
        try { return Ok(await analytics.AuditAsync(access.Id, query, HttpContext.RequestAborted)); }
        catch (ArgumentException) { return this.ApiError("reports.invalidQuery"); }
    }

    private async Task<IActionResult> Page<T>(string guildId, string? range, Func<ulong, AnalyticsRange, CancellationToken, Task<T>> query)
    {
        if (!GuildAnalyticsQueryService.TryParseRange(range, out var parsed)) return this.ApiError("reports.invalidQuery");
        var access = await Access(guildId); if (access.Error != null) return access.Error;
        return Ok(await query(access.Id, parsed, HttpContext.RequestAborted));
    }

    private async Task<(ulong Id, IActionResult? Error)> Access(string value)
    {
        if (!ulong.TryParse(value, out var guildId)) return (0, this.ApiError("guild.invalidId"));
        return await authorization.CanAccessModuleAsync(User, guildId, GuildModuleIds.Analytics, HttpContext.RequestAborted) ? (guildId, null) : (0, Forbid());
    }
}
