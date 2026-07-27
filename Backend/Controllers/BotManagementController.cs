using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Rankoon.Api;
using Rankoon.Data.Analytics;
using Rankoon.Data.Auth;
using Rankoon.Data.Operations;

namespace Rankoon.Controllers;

[ApiController, EnableRateLimiting("bot-management"), Route("api/bot-management")]
public sealed class BotManagementController(IBotOperatorAccessService access, IOperationsQueryService operations) : ControllerBase
{
    [HttpGet("access"), Authorize]
    public async Task<IActionResult> GetAccess()
    {
        if (!Actor(out var userId)) return this.ApiError("auth.tokenInvalid"); var result = await access.GetAccessAsync(userId, HttpContext.RequestAborted);
        return !result.IsAvailable ? this.ApiError("botManagement.unavailable") : Ok(new { isBotOperator = result.IsAuthorized, role = result.Role });
    }

    [HttpGet("operations/overview"), HttpGet("overview"), Authorize(Policy = AuthorizationPolicies.BotOperator)] public async Task<IActionResult> Overview([FromQuery] string? range) => TryRange(range, operations.OverviewAsync, out var task) ? Ok(await task) : this.ApiError("botManagement.invalidRange");
    [HttpGet("guild-health"), HttpGet("guilds"), Authorize(Policy = AuthorizationPolicies.BotOperator)] public async Task<IActionResult> GuildHealth([FromQuery] string? range) => TryRange(range, operations.GuildHealthAsync, out var task) ? Ok(await task) : this.ApiError("botManagement.invalidRange");
    [HttpGet("usage"), Authorize(Policy = AuthorizationPolicies.BotOperator)] public async Task<IActionResult> Usage([FromQuery] string? range) => TryRange(range, operations.UsageAsync, out var task) ? Ok(await task) : this.ApiError("botManagement.invalidRange");

    [HttpGet("incidents"), Authorize(Policy = AuthorizationPolicies.BotOperator)]
    public async Task<IActionResult> Incidents([FromQuery] IncidentQuery query) { try { return Ok(await operations.IncidentsAsync(query, HttpContext.RequestAborted)); } catch (ArgumentException) { return this.ApiError("reports.invalidQuery"); } }
    [HttpGet("incidents/{fingerprint}"), Authorize(Policy = AuthorizationPolicies.BotOperator)] public async Task<IActionResult> Incident(string fingerprint) => await operations.IncidentAsync(fingerprint, HttpContext.RequestAborted) is { } value ? Ok(value) : NotFound();
    [HttpPatch("incidents/{fingerprint}"), Authorize(Policy = AuthorizationPolicies.BotOperator)]
    public async Task<IActionResult> Transition(string fingerprint, [FromBody] IncidentTransitionRequest request)
    {
        if (!Actor(out var actor)) return this.ApiError("auth.tokenInvalid");
        try { return await operations.TransitionAsync(fingerprint, request, actor, HttpContext.RequestAborted) is { } value ? Ok(value) : NotFound(); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return this.ApiError("request.rejected"); }
    }
    [HttpGet("errors/{occurrenceId}"), Authorize(Policy = AuthorizationPolicies.BotOperator)] public async Task<IActionResult> Error(string occurrenceId) => await operations.OccurrenceAsync(occurrenceId, HttpContext.RequestAborted) is { } value ? Ok(value) : NotFound();

    [HttpPost("incidents/{fingerprint}/acknowledge"), Authorize(Policy = AuthorizationPolicies.BotOperator)]
    public Task<IActionResult> Acknowledge(string fingerprint) => Transition(fingerprint, new("acknowledge", null));
    [HttpPost("incidents/{fingerprint}/resolve"), Authorize(Policy = AuthorizationPolicies.BotOperator)]
    public Task<IActionResult> Resolve(string fingerprint) => Transition(fingerprint, new("resolve", null));
    [HttpPost("incidents/{fingerprint}/reopen"), Authorize(Policy = AuthorizationPolicies.BotOperator)]
    public Task<IActionResult> Reopen(string fingerprint) => Transition(fingerprint, new("reopen", null));
    [HttpPost("incidents/{fingerprint}/ignore"), Authorize(Policy = AuthorizationPolicies.BotOperator)]
    public Task<IActionResult> Ignore(string fingerprint) => Transition(fingerprint, new("ignore", null));

    private bool TryRange<T>(string? value, Func<AnalyticsRange, CancellationToken, Task<T>> query, out Task<T> task) { if (GuildAnalyticsQueryService.TryParseRange(value, out var range)) { task = query(range, HttpContext.RequestAborted); return true; } task = Task.FromResult(default(T)!); return false; }
    private bool Actor(out ulong actor) => ulong.TryParse(User.FindFirst("discord_id")?.Value, out actor);
}
