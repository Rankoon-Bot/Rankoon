using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Rankoon.Api;
using Rankoon.Data.Auth;
using Rankoon.Data.Reporting;

namespace Rankoon.Controllers;

[ApiController]
[EnableRateLimiting("bot-management")]
[Route("api/bot-management")]
public sealed class BotManagementController(IBotOperatorAccessService access, IBotManagementOverviewService overview) : ControllerBase
{
    [HttpGet("access")]
    [Authorize]
    public async Task<IActionResult> GetAccess()
    {
        if (!ulong.TryParse(User.FindFirst("discord_id")?.Value, out var userId)) return this.ApiError("auth.tokenInvalid");
        var result = await access.GetAccessAsync(userId, HttpContext.RequestAborted);
        if (!result.IsAvailable) return this.ApiError("botManagement.unavailable");
        return Ok(new { isBotOperator = result.IsAuthorized, role = result.Role });
    }

    [HttpGet("overview")]
    [Authorize(Policy = AuthorizationPolicies.BotOperator)]
    public async Task<IActionResult> GetOverview([FromQuery] string? range)
    {
        if (!BotManagementOverviewService.TryParseRange(range, out var parsed)) return this.ApiError("botManagement.invalidRange");
        return Ok(await overview.GetOverviewAsync(parsed, HttpContext.RequestAborted));
    }
}
