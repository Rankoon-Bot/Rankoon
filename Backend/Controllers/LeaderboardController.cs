using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Microsoft.AspNetCore.RateLimiting;
using Rankoon.Data.Discord;
using Rankoon.Data.Auth;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Rankoon.Data.Reporting;

namespace Rankoon.Controllers;

public sealed record LeaderboardSettingsRequest(string Alias, LeaderboardVisibility Visibility);
public sealed record LeaderboardPrivacyRequest(bool PublicVisible);

[ApiController]
[EnableRateLimiting("leaderboard")]
[Route("api/rankings")]
public sealed class LeaderboardController(LeaderboardService leaderboard, IGuildAuthorizationService authorization, IReportWriter reports) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("{alias}")]
    public async Task<IActionResult> Page(string alias, [FromQuery] string? cursor, [FromQuery] int take = 25, [FromQuery] bool aroundMe = false)
    {
        var settings = await leaderboard.FindSettingsAsync(alias, HttpContext.RequestAborted);
        if (settings == null) return NotFound();
        var isMember = User.Identity?.IsAuthenticated == true && await authorization.IsMemberAsync(User, settings.GuildId, HttpContext.RequestAborted);
        if (settings.Visibility == LeaderboardVisibility.MembersOnly && !isMember)
            return User.Identity?.IsAuthenticated == true ? Forbid() : Unauthorized();
        var userId = authorization.GetDiscordUserId(User);
        try
        {
            return Ok(await leaderboard.GetPageAsync(settings, isMember, userId, cursor, take, aroundMe && isMember, HttpContext.RequestAborted));
        }
        catch (FormatException)
        {
            return BadRequest(new { error = "Invalid leaderboard cursor." });
        }
    }

    [Authorize]
    [HttpPut("{alias}/me/privacy")]
    public async Task<IActionResult> Privacy(string alias, [FromBody] LeaderboardPrivacyRequest request)
    {
        var settings = await leaderboard.FindSettingsAsync(alias, HttpContext.RequestAborted);
        var userId = authorization.GetDiscordUserId(User);
        if (settings == null || userId == null) return NotFound();
        if (!await authorization.IsMemberAsync(User, settings.GuildId, HttpContext.RequestAborted)) return Forbid();
        await leaderboard.SetPublicVisibilityAsync(settings.GuildId, userId.Value, request.PublicVisible, HttpContext.RequestAborted);
        await reports.WriteAsync(new(settings.GuildId, ReportCategories.Activity, ReportNames.LeaderboardPrivacyChanged, ReportOutcomes.Succeeded, ActorId: userId, Metadata: new Dictionary<string, object?> { ["enabled"] = request.PublicVisible }), HttpContext.RequestAborted);
        return Ok(new { request.PublicVisible });
    }
}

[ApiController]
[Authorize]
[Route("api/guilds/{guildId}/leaderboard-settings")]
public sealed class LeaderboardSettingsController(LeaderboardService leaderboard, IGuildAuthorizationService authorization, Discord.WebSocket.DiscordShardedClient discord, GuildMembershipService memberships, IReportWriter reports) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id)) return BadRequest();
        if (!await authorization.CanManageAsync(User, id, HttpContext.RequestAborted)) return Forbid();
        var guild = discord.GetGuild(id);
        if (guild == null) return NotFound();
        return Ok(await leaderboard.GetOrCreateSettingsAsync(id, guild.Name, HttpContext.RequestAborted));
    }

    [HttpPut]
    public async Task<IActionResult> Save(string guildId, [FromBody] LeaderboardSettingsRequest request)
    {
        if (!ulong.TryParse(guildId, out var id)) return BadRequest();
        if (!await authorization.CanManageAsync(User, id, HttpContext.RequestAborted)) return Forbid();
        var guild = discord.GetGuild(id);
        if (guild == null) return NotFound();
        try
        {
            var current = await leaderboard.GetOrCreateSettingsAsync(id, guild.Name, HttpContext.RequestAborted);
            if (request.Visibility == LeaderboardVisibility.Public && current.Visibility != LeaderboardVisibility.Public)
                await memberships.ReconcileGuildAsync(id, HttpContext.RequestAborted);
            var saved = await leaderboard.SaveSettingsAsync(id, guild.Name, request.Alias, request.Visibility, HttpContext.RequestAborted);
            await reports.WriteAsync(new(id, ReportCategories.Activity, ReportNames.LeaderboardSettingsChanged, ReportOutcomes.Succeeded, ActorId: authorization.GetDiscordUserId(User), Metadata: new Dictionary<string, object?> { ["state"] = request.Visibility }), HttpContext.RequestAborted);
            return Ok(saved);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict(new { error = "Dieser Alias wird bereits verwendet." });
        }
    }
}
