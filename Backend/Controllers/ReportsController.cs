using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rankoon.Data.Auth;
using Rankoon.Data.Reporting;
using Microsoft.AspNetCore.RateLimiting;

namespace Rankoon.Controllers;

[ApiController]
[Authorize]
[EnableRateLimiting("reports")]
[Route("api/guilds/{guildId}/reports")]
public sealed class ReportsController(IGuildAuthorizationService authorization, IReportQueryService reports) : ControllerBase
{
    [HttpGet("activity")]
    public Task<IActionResult> Activity(string guildId, [FromQuery] ReportQuery query) => ListAsync(guildId, ReportCategories.Activity, query);

    [HttpGet("activity/summary")]
    public Task<IActionResult> ActivitySummary(string guildId, [FromQuery] ReportQuery query) => SummaryAsync(guildId, ReportCategories.Activity, query);

    [HttpGet("commands")]
    public Task<IActionResult> Commands(string guildId, [FromQuery] ReportQuery query) => ListAsync(guildId, ReportCategories.Command, query);

    [HttpGet("commands/summary")]
    public Task<IActionResult> CommandsSummary(string guildId, [FromQuery] ReportQuery query) => SummaryAsync(guildId, ReportCategories.Command, query);

    [HttpGet("errors")]
    public Task<IActionResult> Errors(string guildId, [FromQuery] ReportQuery query) => ListAsync(guildId, ReportCategories.Error, query);

    [HttpGet("errors/summary")]
    public Task<IActionResult> ErrorsSummary(string guildId, [FromQuery] ReportQuery query) => SummaryAsync(guildId, ReportCategories.Error, query);

    private async Task<IActionResult> ListAsync(string guildId, string category, ReportQuery query)
    {
        var id = await AuthorizeAsync(guildId);
        if (id.Error != null) return id.Error;
        try { return Ok(await reports.ListAsync(id.GuildId, category, query, HttpContext.RequestAborted)); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    private async Task<IActionResult> SummaryAsync(string guildId, string category, ReportQuery query)
    {
        var id = await AuthorizeAsync(guildId);
        if (id.Error != null) return id.Error;
        try { return Ok(await reports.SummarizeAsync(id.GuildId, category, query, HttpContext.RequestAborted)); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    private async Task<(ulong GuildId, IActionResult? Error)> AuthorizeAsync(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id)) return (0, BadRequest(new { error = "Invalid guild ID" }));
        if (!await authorization.CanAccessModuleAsync(User, id, GuildModuleIds.Reporting, HttpContext.RequestAborted)) return (0, Forbid());
        return (id, null);
    }
}
