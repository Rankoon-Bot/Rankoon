using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MongoDB.Driver;
using Rankoon.Api;
using Rankoon.Data.Auth;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;

namespace Rankoon.Controllers;

public sealed record SetGuildMaintainerAccessRequest(bool Enabled, long Revision);
public sealed record MaintainerGuildResponse(string GuildId, string Name, string? IconUrl, int MemberCount, bool Enabled, long Revision, bool RuntimeAvailable, BotIdentityMode? ActiveBotIdentity);

[ApiController, Authorize(Policy = AuthorizationPolicies.BotMaintainer), EnableRateLimiting("bot-management"), Route("api/bot-management/maintainer")]
public sealed class BotMaintainerController(
    IPlatformBotRuntime platform,
    IBotRuntimeManager runtimes,
    IGuildDiscordContextResolver guildResolver,
    RankoonDbContext database,
    IGuildMaintainerAccessService maintainerAccess,
    IReportWriter reports) : ControllerBase
{
    [HttpGet("guilds")]
    public async Task<IActionResult> Guilds()
    {
        var token = HttpContext.RequestAborted;
        var customGuildIds = await database.GuildBotIdentities.Find(x => x.EncryptedBotToken != null).Project(x => x.GuildId).ToListAsync(token);
        var guildIds = platform.Client.Guilds.Select(x => x.Id)
            .Concat(runtimes.GetRuntimeSnapshots().Where(x => x.Mode == BotIdentityMode.Custom && x.GuildId.HasValue).Select(x => x.GuildId!.Value))
            .Concat(customGuildIds)
            .Distinct()
            .ToArray();
        var contexts = await guildResolver.ResolveManyAsync(guildIds, token);
        var settings = await maintainerAccess.GetManyAsync(guildIds, token);
        var rows = guildIds.Select(guildId =>
        {
            contexts.TryGetValue(guildId, out var context);
            var access = settings[guildId];
            return new MaintainerGuildResponse(
                guildId.ToString(),
                context?.Guild.Name ?? $"Guild {guildId}",
                context?.Guild.IconUrl,
                context?.Guild.MemberCount ?? 0,
                access.Enabled,
                access.Revision,
                context != null,
                context?.IdentityMode);
        }).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return Ok(rows);
    }

    [HttpPut("guilds/{guildId}")]
    public async Task<IActionResult> SetGuildAccess(string guildId, [FromBody] SetGuildMaintainerAccessRequest request)
    {
        if (!ulong.TryParse(guildId, out var id) || id == 0) return this.ApiError("guild.invalidId");
        if (!ulong.TryParse(User.FindFirst("discord_id")?.Value, out var actorId)) return this.ApiError("auth.tokenInvalid");

        var context = await guildResolver.ResolveAsync(id, HttpContext.RequestAborted);
        var hasConfiguredCustomBot = await database.GuildBotIdentities.Find(x => x.GuildId == id && x.EncryptedBotToken != null).AnyAsync(HttpContext.RequestAborted);
        if (context == null && platform.Client.GetGuild(id) == null && !hasConfiguredCustomBot) return NotFound();

        var previous = await maintainerAccess.GetAsync(id, HttpContext.RequestAborted);
        var saved = await maintainerAccess.SetAsync(id, request.Enabled, request.Revision, actorId, HttpContext.RequestAborted);
        if (saved == null) return this.ApiError("botManagement.maintainerRevisionConflict");

        await reports.WriteAsync(new(
            id,
            ReportCategories.Activity,
            ReportNames.MaintainerAccessChanged,
            ReportOutcomes.Succeeded,
            ActorId: actorId,
            Metadata: new Dictionary<string, object?>
            {
                ["previouslyEnabled"] = previous.Enabled,
                ["enabled"] = saved.Enabled,
                ["revision"] = saved.Revision
            }), HttpContext.RequestAborted);

        return Ok(new { guildId = id.ToString(), enabled = saved.Enabled, revision = saved.Revision, updatedAtUtc = saved.UpdatedAtUtc });
    }
}
