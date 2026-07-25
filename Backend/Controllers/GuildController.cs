using System.Text.Json;
using Discord.WebSocket;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Rankoon.Data.Auth;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Xp;
using Rankoon.Data.Xp.Import;
using Rankoon.Data.Reporting;
using Rankoon.Data.Dashboard;
using Rankoon.Api;

namespace Rankoon.Controllers;

[ApiController]
[Authorize]
[Route("api/guilds/{guildId}")]
public sealed class GuildController(IGuildAuthorizationService authorization, IGuildDiscordContextResolver discord, RankoonDbContext database, IXpService xp, IXpImportService xpImport, VoiceXpWatchdog watchdog, VcHubService hubs, IReportWriter reports, IDashboardOverviewService dashboard) : ControllerBase
{
    private async Task<(ulong Id, IActionResult? Error)> AuthorizeGuildAsync(string guildId, string? moduleId = null)
    {
        if (!ulong.TryParse(guildId, out var id)) return (0, this.ApiError("guild.invalidId"));
        var canAccess = moduleId == null
            ? await authorization.CanAccessAnyModuleAsync(User, id, HttpContext.RequestAborted)
            : await authorization.CanAccessModuleAsync(User, id, moduleId, HttpContext.RequestAborted);
        if (!canAccess) return (0, Forbid());
        return (id, null);
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(string guildId, [FromQuery] string? period = null)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId); if (error != null) return error;
        var selected = string.IsNullOrWhiteSpace(period) ? DashboardPeriod.SevenDays : Enum.TryParse<DashboardPeriod>(period, true, out var value) && Enum.IsDefined(value) ? value : (DashboardPeriod?)null;
        if (selected == null) return this.ApiError("dashboard.invalidPeriod");
        return Ok(await dashboard.GetAsync(User, id, selected.Value, HttpContext.RequestAborted));
    }

    [HttpGet("resources")]
    public async Task<IActionResult> Resources(string guildId)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId); if (error != null) return error;
        var guild = (await discord.ResolveAsync(id, HttpContext.RequestAborted))?.Guild; if (guild == null) return NotFound();
        return Ok(new { roles = guild.Roles.Where(x => !x.IsManaged && !x.IsEveryone).Select(x => new { id = x.Id, name = x.Name }), channels = guild.Channels.Select(x => new { id = x.Id, name = x.Name, type = x.GetType().Name }) });
    }

    [HttpGet("xp/config")]
    public async Task<IActionResult> GetXpConfig(string guildId) { var (id, error) = await AuthorizeGuildAsync(guildId, GuildModuleIds.Xp); return error ?? Ok(await xp.GetSettingsAsync(id, HttpContext.RequestAborted)); }
    [HttpPut("xp/config")]
    public async Task<IActionResult> SaveXpConfig(string guildId, [FromBody] GuildXpSettings settings)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId, GuildModuleIds.Xp); if (error != null) return error;
        var validationErrors = ValidateXpSettings(settings);
        if (validationErrors.Count > 0)
        {
            var errors = validationErrors
                .GroupBy(error => error.Field, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<ApiValidationError>)group.Select(error => ApiErrorFactory.Validation(error.ErrorKey)).ToArray(),
                    StringComparer.Ordinal);
            return this.ApiError("xp.settingsInvalid", errors: errors);
        }
        settings.GuildId = id;
        settings.Voice.MinimumSessionSeconds = Math.Clamp(settings.Voice.MinimumSessionSeconds, 0, 86400);
        ServerBoosterXpSettingsValidator.Normalize(settings.ServerBooster);
        // Settle open intervals with the persisted revision before changing their qualification or rate.
        await watchdog.ReconcileNowAsync(id, HttpContext.RequestAborted);
        await xp.SaveSettingsAsync(settings, HttpContext.RequestAborted);
        await watchdog.ReconcileNowAsync(id, HttpContext.RequestAborted);
        await WriteActivityAsync(id, ReportNames.XpSettingsChanged, metadata: new Dictionary<string, object?> { ["enabled"] = settings.Enabled });
        return Ok(settings);
    }

    [HttpGet("xp/watchdog")]
    public async Task<IActionResult> GetVoiceWatchdog(string guildId)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId, GuildModuleIds.Xp); if (error != null) return error;
        return Ok(watchdog.GetStatus(id));
    }

    [HttpGet("xp/leaderboard")]
    public async Task<IActionResult> Leaderboard(string guildId, [FromQuery] int take = 25) { var (id, error) = await AuthorizeGuildAsync(guildId, GuildModuleIds.Xp); return error ?? Ok((await xp.GetLeaderboardAsync(id, take, HttpContext.RequestAborted)).Select(ToRank)); }
    [HttpGet("xp/members/{userId}")]
    public async Task<IActionResult> Member(string guildId, string userId)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId, GuildModuleIds.Xp); if (error != null) return error;
        if (!ulong.TryParse(userId, out var user)) return this.ApiError("user.invalidId");
        var member = await xp.GetMemberAsync(id, user, HttpContext.RequestAborted); return member == null ? NotFound() : Ok(ToRank(member));
    }

    [HttpPost("xp/import")]
    [HttpPost("xp/import/mee6")]
    public async Task<IActionResult> ImportXpJson(string guildId, [FromBody] JsonElement payload)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId, GuildModuleIds.Xp); if (error != null) return error;
        XpImportResult result;
        try
        {
            result = await xpImport.ImportAsync(id, payload, HttpContext.RequestAborted);
        }
        catch (XpImportParseException exception)
        {
            return this.ApiError(exception.ErrorKey);
        }
        await WriteActivityAsync(id, ReportNames.XpJsonImported, metadata: new Dictionary<string, object?>
        {
            ["format"] = result.Format.ToString(),
            ["imported"] = result.Imported,
            ["skippedInvalid"] = result.SkippedInvalid,
            ["skippedForeignGuild"] = result.SkippedForeignGuild,
            ["duplicateUsers"] = result.DuplicateUsers
        });
        return Ok(result);
    }

    [HttpGet("vc-hubs")]
    public async Task<IActionResult> Hubs(string guildId) { var (id, error) = await AuthorizeGuildAsync(guildId, GuildModuleIds.VoiceHubs); return error ?? Ok(await database.VcHubs.Find(x => x.GuildId == id).ToListAsync(HttpContext.RequestAborted)); }
    [HttpPost("vc-hubs")]
    public async Task<IActionResult> CreateHub(string guildId, [FromBody] VcHub hub)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId, GuildModuleIds.VoiceHubs); if (error != null) return error;
        hub.Id = null; hub.GuildId = id;
        if (hub.JoinChannelId == 0)
        {
            var guild = (await discord.ResolveAsync(id, HttpContext.RequestAborted))?.Guild; if (guild == null) return NotFound();
            var created = await guild.CreateVoiceChannelAsync(string.IsNullOrWhiteSpace(hub.HubChannelName) ? "VC erstellen" : hub.HubChannelName, options => options.CategoryId = hub.CategoryId);
            hub.JoinChannelId = created.Id;
            hub.IsManagedChannel = true;
        }
        else hub.IsManagedChannel = false;
        await database.VcHubs.InsertOneAsync(hub, cancellationToken: HttpContext.RequestAborted);
        await WriteActivityAsync(id, ReportNames.VoiceHubCreated, metadata: new Dictionary<string, object?> { ["hubId"] = hub.Id, ["channelId"] = hub.JoinChannelId });
        return Ok(hub);
    }
    [HttpPut("vc-hubs/{hubId}")]
    public async Task<IActionResult> UpdateHub(string guildId, string hubId, [FromBody] VcHub hub)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId, GuildModuleIds.VoiceHubs); if (error != null) return error;
        var existingHub = await database.VcHubs.Find(x => x.GuildId == id && x.Id == hubId).FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (existingHub == null) return NotFound();

        hub.Id = hubId;
        hub.GuildId = id;
        var guild = (await discord.ResolveAsync(id, HttpContext.RequestAborted))?.Guild; if (guild == null) return NotFound();
        await hubs.UpdateHubAsync(guild, existingHub, hub, HttpContext.RequestAborted);
        await WriteActivityAsync(id, ReportNames.VoiceHubUpdated, metadata: new Dictionary<string, object?> { ["hubId"] = hubId });
        return Ok(hub);
    }
    [HttpDelete("vc-hubs/{hubId}")]
    public async Task<IActionResult> DeleteHub(string guildId, string hubId)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId, GuildModuleIds.VoiceHubs); if (error != null) return error;
        var hub = await database.VcHubs.Find(x => x.GuildId == id && x.Id == hubId).FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (hub == null) return NotFound();
        var guild = (await discord.ResolveAsync(id, HttpContext.RequestAborted))?.Guild; if (guild == null) return NotFound();
        await hubs.DeleteHubAsync(guild, hub, HttpContext.RequestAborted);
        await WriteActivityAsync(id, ReportNames.VoiceHubDeleted, metadata: new Dictionary<string, object?> { ["hubId"] = hubId, ["channelId"] = hub.JoinChannelId });
        return NoContent();
    }

    private static object ToRank(MemberXp member) { var total = member.TotalXp; return new { member.UserId, member.DisplayName, totalXp = decimal.Truncate(total), level = Mee6LevelCurve.GetLevel(total), member.MessageCount, member.VoiceSeconds }; }

    private Task WriteActivityAsync(ulong guildId, string name, string? action = null, IReadOnlyDictionary<string, object?>? metadata = null) =>
        reports.WriteAsync(new(guildId, ReportCategories.Activity, name, ReportOutcomes.Succeeded, action, authorization.GetDiscordUserId(User), Metadata: metadata), HttpContext.RequestAborted);

    private static List<(string Field, string ErrorKey)> ValidateXpSettings(GuildXpSettings settings)
    {
        var errors = new List<(string Field, string ErrorKey)>();
        if (settings.Message == null || settings.Voice == null || settings.Reaction == null || settings.EventInterest == null || settings.Thread == null || settings.ServerBooster == null)
        {
            errors.Add(("settings", "xp.settings.groupsRequired"));
            return errors;
        }
        if (settings.ExcludedChannelIds == null || settings.ExcludedCategoryIds == null || settings.ExcludedRoleIds == null || settings.ChannelMultipliers == null || settings.LevelRoles == null)
        {
            errors.Add(("settings", "xp.settings.collectionsRequired"));
            return errors;
        }
        if (settings.Message.MinimumPoints < 0 || settings.Message.MaximumPoints < settings.Message.MinimumPoints) errors.Add(("message.points", "xp.settings.messagePoints"));
        if (settings.Message.MinimumCharacters < 0 || settings.Message.MaximumCharacters < settings.Message.MinimumCharacters) errors.Add(("message.characters", "xp.settings.messageCharacters"));
        if (settings.Message.CooldownSeconds < 0) errors.Add(("message.cooldownSeconds", "xp.settings.messageCooldown"));
        if (settings.Voice.PointsPerMinute < 0) errors.Add(("voice.pointsPerMinute", "xp.settings.voicePoints"));
        if (settings.Voice.MinimumSessionSeconds is < 0 or > 86400) errors.Add(("voice.timing", "xp.settings.voiceTiming"));
        if (settings.Reaction.Points < 0 || settings.Reaction.CooldownSeconds < 0) errors.Add(("reaction", "xp.settings.reaction"));
        if (settings.EventInterest.Points < 0) errors.Add(("eventInterest.points", "xp.settings.eventInterest"));
        if (settings.Thread.CreatePoints < 0 || settings.Thread.MessagePoints < 0 || settings.Thread.CooldownSeconds < 0) errors.Add(("thread", "xp.settings.thread"));
        if (settings.ChannelMultipliers.Any(x => x.ChannelId == 0 || x.Multiplier < 0) || settings.ChannelMultipliers.Select(x => x.ChannelId).Distinct().Count() != settings.ChannelMultipliers.Count) errors.Add(("channelMultipliers", "xp.settings.channelMultipliers"));
        if (settings.LevelRoles.Any(x => x.Level < 1 || x.RoleId == 0) || settings.LevelRoles.Select(x => x.RoleId).Distinct().Count() != settings.LevelRoles.Count) errors.Add(("levelRoles", "xp.settings.levelRoles"));
        errors.AddRange(ServerBoosterXpSettingsValidator.Validate(settings.ServerBooster));
        return errors;
    }
}
