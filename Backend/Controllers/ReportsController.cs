using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rankoon.Data.Auth;
using Rankoon.Data.Reporting;
using Microsoft.AspNetCore.RateLimiting;
using Rankoon.Api;

namespace Rankoon.Controllers;

[ApiController]
[Authorize]
[EnableRateLimiting("reports")]
[Route("api/guilds/{guildId}/reports")]
public sealed class ReportsController(IGuildAuthorizationService authorization, IReportQueryService reports) : ControllerBase
{
    [HttpGet("activity")]
    [Obsolete("Use guild analytics.")]
    public Task<IActionResult> Activity(string guildId, [FromQuery] ReportQuery query) => ListAsync(guildId, ReportCategories.Activity, query);

    [HttpGet("activity/summary")]
    [Obsolete("Use guild analytics.")]
    public Task<IActionResult> ActivitySummary(string guildId, [FromQuery] ReportQuery query) => SummaryAsync(guildId, ReportCategories.Activity, query);

    [HttpGet("commands")]
    [Obsolete("Use guild analytics.")]
    public Task<IActionResult> Commands(string guildId, [FromQuery] ReportQuery query) => ListAsync(guildId, ReportCategories.Command, query);

    [HttpGet("commands/summary")]
    [Obsolete("Use guild analytics.")]
    public Task<IActionResult> CommandsSummary(string guildId, [FromQuery] ReportQuery query) => SummaryAsync(guildId, ReportCategories.Command, query);

    [HttpGet("errors")]
    [Obsolete("Gone. Use bot-management incidents.")]
    public IActionResult Errors() => StatusCode(StatusCodes.Status410Gone);

    [HttpGet("errors/summary")]
    [Obsolete("Gone. Use bot-management incidents.")]
    public IActionResult ErrorsSummary() => StatusCode(StatusCodes.Status410Gone);

    private async Task<IActionResult> ListAsync(string guildId, string category, ReportQuery query)
    {
        var id = await AuthorizeAsync(guildId);
        if (id.Error != null) return id.Error;
        try { return Ok(await reports.ListAsync(id.GuildId, category, query, HttpContext.RequestAborted)); }
        catch (ArgumentException) { return this.ApiError("reports.invalidQuery"); }
    }

    private async Task<IActionResult> SummaryAsync(string guildId, string category, ReportQuery query)
    {
        var id = await AuthorizeAsync(guildId);
        if (id.Error != null) return id.Error;
        try { return Ok(await reports.SummarizeAsync(id.GuildId, category, query, HttpContext.RequestAborted)); }
        catch (ArgumentException) { return this.ApiError("reports.invalidQuery"); }
    }

    private async Task<(ulong GuildId, IActionResult? Error)> AuthorizeAsync(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id)) return (0, this.ApiError("guild.invalidId"));
        if (!await authorization.CanAccessModuleAsync(User, id, GuildModuleIds.Reporting, HttpContext.RequestAborted)) return (0, Forbid());
        return (id, null);
    }
}
