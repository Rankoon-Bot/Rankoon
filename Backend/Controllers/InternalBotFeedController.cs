using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Rankoon.Api;
using Rankoon.Data.Auth;
using Rankoon.Data.Operations;

namespace Rankoon.Controllers;

[ApiController, Authorize(Policy = AuthorizationPolicies.BotMaintainer), EnableRateLimiting("bot-management"), Route("api/bot-management/internal-feed")]
public sealed class InternalBotFeedController(InternalBotFeedSettingsStore settingsStore) : ControllerBase
{
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var settings = await settingsStore.GetAsync(HttpContext.RequestAborted);
        return Ok(settingsStore.ToResponse(settings));
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings([FromBody] UpdateInternalBotFeedSettingsRequest request)
    {
        if (request.Revision < 1 || request.DailySummaryHourUtc is < 0 or > 23 || request.DailySummaryMinuteUtc is < 0 or > 59)
            return this.ApiError("botManagement.feedSettingsInvalid");

        ulong channelId = 0;
        if (!string.IsNullOrWhiteSpace(request.ChannelId) && (!ulong.TryParse(request.ChannelId, out channelId) || channelId == 0))
            return this.ApiError("botManagement.feedSettingsInvalid");
        var hasOutput = request.Enabled || request.DailySummaryEnabled;
        if (hasOutput && channelId == 0)
            return this.ApiError("botManagement.feedSettingsInvalid");
        if (hasOutput && !settingsStore.SupportGuildAvailable)
            return this.ApiError("botManagement.feedUnavailable");
        if (hasOutput && !settingsStore.CanBotSendTo(channelId))
            return this.ApiError("botManagement.feedChannelUnavailable");

        var saved = await settingsStore.SaveAsync(request, channelId, HttpContext.RequestAborted);
        return saved == null
            ? this.ApiError("botManagement.feedRevisionConflict")
            : Ok(settingsStore.ToResponse(saved));
    }
}
