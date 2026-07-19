using System.Text.Json;
using Discord.WebSocket;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using MongoDB.Bson;
using Rankoon.Data.Auth;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Xp;
using Rankoon.Data.Reporting;
using Rankoon.Api;

namespace Rankoon.Controllers;

public sealed record VoiceWatchdogControl(bool Enabled);

[ApiController]
[Authorize]
[Route("api/guilds/{guildId}")]
public sealed class GuildController(IGuildAuthorizationService authorization, DiscordShardedClient discord, RankoonDbContext database, IXpService xp, LeaderboardService leaderboard, GuildMembershipService memberships, VoiceXpWatchdog watchdog, VcHubService hubs, IReportWriter reports) : ControllerBase
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
    public async Task<IActionResult> Dashboard(string guildId)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId); if (error != null) return error;
        var guild = discord.GetGuild(id)!;
        var stats = await database.GuildStats.Find(x => x.GuildId == id).FirstOrDefaultAsync() ?? new GuildStats { GuildId = id };
        var botCount = guild.Users.Count(user => user.IsBot);
        var activeVc = guild.VoiceChannels.Sum(x => x.ConnectedUsers.Count(user => !user.IsBot));
        var activeTemp = await database.TemporaryVoiceChannels.CountDocumentsAsync(x => x.GuildId == id);
        var top = await xp.GetLeaderboardAsync(id, 5, HttpContext.RequestAborted);
        var leaderboardSettings = await leaderboard.GetOrCreateSettingsAsync(id, guild.Name, HttpContext.RequestAborted);
        return Ok(new { guildName = guild.Name, leaderboardAlias = leaderboardSettings.Alias, memberCount = guild.MemberCount - botCount, botCount, activeVoiceMembers = activeVc, activeXpMembers = await database.MemberXp.CountDocumentsAsync(x => x.GuildId == id), stats = new { xpAwarded = decimal.Truncate(stats.XpAwarded), stats.Messages, stats.Reactions, stats.Threads, stats.EventInterests, stats.TemporaryChannelsCreated }, activeTemporaryChannels = activeTemp, processUptimeSeconds = (long)(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds, watchdog = watchdog.GetStatus(id), leaderboard = top.Select(ToRank) });
    }

    [HttpGet("resources")]
    public async Task<IActionResult> Resources(string guildId)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId); if (error != null) return error;
        var guild = discord.GetGuild(id)!;
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
        settings.Voice.CheckIntervalSeconds = Math.Clamp(settings.Voice.CheckIntervalSeconds, 15, 300);
        settings.Voice.MinimumSessionSeconds = Math.Clamp(settings.Voice.MinimumSessionSeconds, 0, 86400);
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

    [HttpPut("xp/watchdog")]
    public async Task<IActionResult> SetVoiceWatchdog(string guildId, [FromBody] VoiceWatchdogControl control)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId, GuildModuleIds.Xp); if (error != null) return error;
        var settings = await xp.GetSettingsAsync(id, HttpContext.RequestAborted);
        settings.Voice.Enabled = control.Enabled;
        if (control.Enabled) settings.Enabled = true;
        await xp.SaveSettingsAsync(settings, HttpContext.RequestAborted);
        await watchdog.ReconcileNowAsync(id, HttpContext.RequestAborted);
        await WriteActivityAsync(id, ReportNames.VoiceWatchdogChanged, metadata: new Dictionary<string, object?> { ["enabled"] = control.Enabled });
        return Ok(new { settings, status = watchdog.GetStatus(id) });
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

    [HttpPost("xp/import/mee6")]
    public async Task<IActionResult> ImportMee6(string guildId, [FromBody] JsonElement payload)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId, GuildModuleIds.Xp); if (error != null) return error;
        if (!payload.TryGetProperty("guild", out var importGuild) || !importGuild.TryGetProperty("id", out var importGuildId) || importGuildId.GetString() != guildId) return this.ApiError("xp.import.guildMismatch");
        if (!payload.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array) return this.ApiError("xp.import.invalidPlayers");
        var imported = 0;
        foreach (var player in players.EnumerateArray())
        {
            if (!player.TryGetProperty("id", out var playerId) || !ulong.TryParse(playerId.GetString(), out var userId)) continue;
            var points = player.TryGetProperty("xp", out var value) ? value.GetInt64() : 0;
            var messages = player.TryGetProperty("message_count", out var messageCount) ? messageCount.GetInt64() : 0;
            var name = player.TryGetProperty("username", out var userName) ? userName.GetString() ?? userId.ToString() : userId.ToString();
            var preference = await database.MemberLeaderboardPreferences.Find(x => x.GuildId == id && x.UserId == userId).FirstOrDefaultAsync(HttpContext.RequestAborted);
            var importUpdate = new PipelineUpdateDefinition<MemberXp>(new BsonDocument[]
            {
                new("$set", new BsonDocument
                {
                    { "guild_id", new BsonDocument("$ifNull", new BsonArray { "$guild_id", new BsonDecimal128((decimal)id) }) },
                    { "user_id", new BsonDocument("$ifNull", new BsonArray { "$user_id", new BsonDecimal128((decimal)userId) }) },
                    { "display_name", name }, { "imported_mee6_xp", points }, { "message_count", messages }, { "updated_at", DateTime.UtcNow },
                    { "is_current_member", new BsonDocument("$ifNull", new BsonArray { "$is_current_member", false }) },
                    { "public_leaderboard_visible", new BsonDocument("$ifNull", new BsonArray { "$public_leaderboard_visible", preference?.PublicVisible ?? true }) }
                }),
                new("$set", new BsonDocument("total_xp", new BsonDocument("$add", new BsonArray
                {
                    new BsonDocument("$ifNull", new BsonArray { "$imported_mee6_xp", 0 }),
                    new BsonDocument("$ifNull", new BsonArray { "$earned_xp", 0 }),
                    new BsonDocument("$ifNull", new BsonArray { "$manual_adjustment", 0 })
                })))
            });
            await database.MemberXp.UpdateOneAsync(x => x.GuildId == id && x.UserId == userId, importUpdate, new UpdateOptions { IsUpsert = true }, HttpContext.RequestAborted);
            imported++;
        }
        memberships.QueueGuild(id);
        await WriteActivityAsync(id, ReportNames.Mee6Imported, metadata: new Dictionary<string, object?> { ["imported"] = imported });
        return Ok(new { imported });
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
            var guild = discord.GetGuild(id)!;
            var created = await guild.CreateVoiceChannelAsync(string.IsNullOrWhiteSpace(hub.HubChannelName) ? "VC erstellen" : hub.HubChannelName, options => options.CategoryId = hub.CategoryId);
            hub.JoinChannelId = created.Id;
        }
        await database.VcHubs.InsertOneAsync(hub, cancellationToken: HttpContext.RequestAborted);
        await WriteActivityAsync(id, ReportNames.VoiceHubCreated, metadata: new Dictionary<string, object?> { ["hubId"] = hub.Id, ["channelId"] = hub.JoinChannelId });
        return Ok(hub);
    }
    [HttpPut("vc-hubs/{hubId}")]
    public async Task<IActionResult> UpdateHub(string guildId, string hubId, [FromBody] VcHub hub) { var (id, error) = await AuthorizeGuildAsync(guildId, GuildModuleIds.VoiceHubs); if (error != null) return error; hub.Id = hubId; hub.GuildId = id; var result = await database.VcHubs.ReplaceOneAsync(x => x.GuildId == id && x.Id == hubId, hub, cancellationToken: HttpContext.RequestAborted); if (result.MatchedCount == 0) return NotFound(); await WriteActivityAsync(id, ReportNames.VoiceHubUpdated, metadata: new Dictionary<string, object?> { ["hubId"] = hubId }); return Ok(hub); }
    [HttpDelete("vc-hubs/{hubId}")]
    public async Task<IActionResult> DeleteHub(string guildId, string hubId)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId, GuildModuleIds.VoiceHubs); if (error != null) return error;
        var hub = await database.VcHubs.Find(x => x.GuildId == id && x.Id == hubId).FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (hub == null) return NotFound();
        await hubs.DeleteHubAsync(discord.GetGuild(id)!, hub, HttpContext.RequestAborted);
        await WriteActivityAsync(id, ReportNames.VoiceHubDeleted, metadata: new Dictionary<string, object?> { ["hubId"] = hubId, ["channelId"] = hub.JoinChannelId });
        return NoContent();
    }

    private static object ToRank(MemberXp member) { var total = member.TotalXp; return new { member.UserId, member.DisplayName, totalXp = decimal.Truncate(total), level = Mee6LevelCurve.GetLevel(total), member.MessageCount, member.VoiceSeconds }; }

    private Task WriteActivityAsync(ulong guildId, string name, string? action = null, IReadOnlyDictionary<string, object?>? metadata = null) =>
        reports.WriteAsync(new(guildId, ReportCategories.Activity, name, ReportOutcomes.Succeeded, action, authorization.GetDiscordUserId(User), Metadata: metadata), HttpContext.RequestAborted);

    private static List<(string Field, string ErrorKey)> ValidateXpSettings(GuildXpSettings settings)
    {
        var errors = new List<(string Field, string ErrorKey)>();
        if (settings.Message == null || settings.Voice == null || settings.Reaction == null || settings.EventInterest == null || settings.Thread == null)
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
        if (settings.Voice.MinimumSessionSeconds is < 0 or > 86400 || settings.Voice.CheckIntervalSeconds is < 15 or > 300) errors.Add(("voice.timing", "xp.settings.voiceTiming"));
        if (settings.Reaction.Points < 0 || settings.Reaction.CooldownSeconds < 0) errors.Add(("reaction", "xp.settings.reaction"));
        if (settings.EventInterest.Points < 0) errors.Add(("eventInterest.points", "xp.settings.eventInterest"));
        if (settings.Thread.CreatePoints < 0 || settings.Thread.MessagePoints < 0 || settings.Thread.CooldownSeconds < 0) errors.Add(("thread", "xp.settings.thread"));
        if (settings.ChannelMultipliers.Any(x => x.ChannelId == 0 || x.Multiplier < 0) || settings.ChannelMultipliers.Select(x => x.ChannelId).Distinct().Count() != settings.ChannelMultipliers.Count) errors.Add(("channelMultipliers", "xp.settings.channelMultipliers"));
        if (settings.LevelRoles.Any(x => x.Level < 1 || x.RoleId == 0) || settings.LevelRoles.Select(x => x.RoleId).Distinct().Count() != settings.LevelRoles.Count) errors.Add(("levelRoles", "xp.settings.levelRoles"));
        return errors;
    }
}
