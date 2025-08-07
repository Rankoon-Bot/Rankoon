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

namespace Rankoon.Controllers;

[ApiController]
[Authorize]
[Route("api/guilds/{guildId}")]
public sealed class GuildController(IGuildAuthorizationService authorization, DiscordShardedClient discord, RankoonDbContext database, IXpService xp, VoiceXpWatchdog watchdog) : ControllerBase
{
    private async Task<(ulong Id, IActionResult? Error)> AuthorizeGuildAsync(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id)) return (0, BadRequest(new { error = "Invalid guild ID" }));
        if (!await authorization.CanManageAsync(User, id, HttpContext.RequestAborted)) return (0, Forbid());
        return (id, null);
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(string guildId)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId); if (error != null) return error;
        var guild = discord.GetGuild(id)!;
        var stats = await database.GuildStats.Find(x => x.GuildId == id).FirstOrDefaultAsync() ?? new GuildStats { GuildId = id };
        var activeVc = guild.VoiceChannels.Sum(x => x.ConnectedUsers.Count(user => !user.IsBot));
        var activeTemp = await database.TemporaryVoiceChannels.CountDocumentsAsync(x => x.GuildId == id);
        var top = await xp.GetLeaderboardAsync(id, 5, HttpContext.RequestAborted);
        return Ok(new { guildName = guild.Name, memberCount = guild.MemberCount, activeVoiceMembers = activeVc, activeXpMembers = await database.MemberXp.CountDocumentsAsync(x => x.GuildId == id), stats, activeTemporaryChannels = activeTemp, processUptimeSeconds = (long)(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds, watchdog = watchdog.GetStatus(id), leaderboard = top.Select(ToRank) });
    }

    [HttpGet("resources")]
    public async Task<IActionResult> Resources(string guildId)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId); if (error != null) return error;
        var guild = discord.GetGuild(id)!;
        return Ok(new { roles = guild.Roles.Where(x => !x.IsManaged && !x.IsEveryone).Select(x => new { id = x.Id, name = x.Name }), channels = guild.Channels.Select(x => new { id = x.Id, name = x.Name, type = x.GetType().Name }) });
    }

    [HttpGet("xp/config")]
    public async Task<IActionResult> GetXpConfig(string guildId) { var (id, error) = await AuthorizeGuildAsync(guildId); return error ?? Ok(await xp.GetSettingsAsync(id, HttpContext.RequestAborted)); }
    [HttpPut("xp/config")]
    public async Task<IActionResult> SaveXpConfig(string guildId, [FromBody] GuildXpSettings settings)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId); if (error != null) return error;
        settings.GuildId = id;
        settings.Voice.CheckIntervalSeconds = Math.Clamp(settings.Voice.CheckIntervalSeconds, 15, 300);
        settings.Voice.MinimumSessionSeconds = Math.Clamp(settings.Voice.MinimumSessionSeconds, 0, 86400);
        await xp.SaveSettingsAsync(settings, HttpContext.RequestAborted);
        return Ok(settings);
    }

    [HttpGet("xp/leaderboard")]
    public async Task<IActionResult> Leaderboard(string guildId, [FromQuery] int take = 25) { var (id, error) = await AuthorizeGuildAsync(guildId); return error ?? Ok((await xp.GetLeaderboardAsync(id, take, HttpContext.RequestAborted)).Select(ToRank)); }
    [HttpGet("xp/members/{userId}")]
    public async Task<IActionResult> Member(string guildId, string userId)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId); if (error != null) return error;
        if (!ulong.TryParse(userId, out var user)) return BadRequest();
        var member = await xp.GetMemberAsync(id, user, HttpContext.RequestAborted); return member == null ? NotFound() : Ok(ToRank(member));
    }

    [HttpPost("xp/import/mee6")]
    public async Task<IActionResult> ImportMee6(string guildId, [FromBody] JsonElement payload)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId); if (error != null) return error;
        if (!payload.TryGetProperty("guild", out var importGuild) || !importGuild.TryGetProperty("id", out var importGuildId) || importGuildId.GetString() != guildId) return BadRequest(new { error = "The MEE6 export belongs to another guild." });
        if (!payload.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array) return BadRequest(new { error = "Invalid MEE6 players export." });
        var imported = 0;
        foreach (var player in players.EnumerateArray())
        {
            if (!player.TryGetProperty("id", out var playerId) || !ulong.TryParse(playerId.GetString(), out var userId)) continue;
            var points = player.TryGetProperty("xp", out var value) ? value.GetInt64() : 0;
            var messages = player.TryGetProperty("message_count", out var messageCount) ? messageCount.GetInt64() : 0;
            var name = player.TryGetProperty("username", out var userName) ? userName.GetString() ?? userId.ToString() : userId.ToString();
            await database.MemberXp.UpdateOneAsync(x => x.GuildId == id && x.UserId == userId, Builders<MemberXp>.Update.SetOnInsert(x => x.GuildId, id).SetOnInsert(x => x.UserId, userId).Set(x => x.DisplayName, name).Set(x => x.ImportedMee6Xp, points).Set(x => x.MessageCount, messages).Set(x => x.UpdatedAt, DateTime.UtcNow), new UpdateOptions { IsUpsert = true });
            imported++;
        }
        return Ok(new { imported });
    }

    [HttpGet("vc-hubs")]
    public async Task<IActionResult> Hubs(string guildId) { var (id, error) = await AuthorizeGuildAsync(guildId); return error ?? Ok(await database.VcHubs.Find(x => x.GuildId == id).ToListAsync(HttpContext.RequestAborted)); }
    [HttpPost("vc-hubs")]
    public async Task<IActionResult> CreateHub(string guildId, [FromBody] VcHub hub)
    {
        var (id, error) = await AuthorizeGuildAsync(guildId); if (error != null) return error;
        hub.Id = null; hub.GuildId = id;
        if (hub.JoinChannelId == 0)
        {
            var guild = discord.GetGuild(id)!;
            var created = await guild.CreateVoiceChannelAsync(string.IsNullOrWhiteSpace(hub.HubChannelName) ? "VC erstellen" : hub.HubChannelName, options => options.CategoryId = hub.CategoryId);
            hub.JoinChannelId = created.Id;
        }
        await database.VcHubs.InsertOneAsync(hub, cancellationToken: HttpContext.RequestAborted);
        return Ok(hub);
    }
    [HttpPut("vc-hubs/{hubId}")]
    public async Task<IActionResult> UpdateHub(string guildId, string hubId, [FromBody] VcHub hub) { var (id, error) = await AuthorizeGuildAsync(guildId); if (error != null) return error; hub.Id = hubId; hub.GuildId = id; var result = await database.VcHubs.ReplaceOneAsync(x => x.GuildId == id && x.Id == hubId, hub, cancellationToken: HttpContext.RequestAborted); return result.MatchedCount == 0 ? NotFound() : Ok(hub); }
    [HttpDelete("vc-hubs/{hubId}")]
    public async Task<IActionResult> DeleteHub(string guildId, string hubId) { var (id, error) = await AuthorizeGuildAsync(guildId); if (error != null) return error; await database.VcHubs.DeleteOneAsync(x => x.GuildId == id && x.Id == hubId, HttpContext.RequestAborted); return NoContent(); }

    private static object ToRank(MemberXp member) { var total = member.ImportedMee6Xp + member.EarnedXp + member.ManualAdjustment; return new { member.UserId, member.DisplayName, totalXp = total, level = Mee6LevelCurve.GetLevel(total), member.MessageCount, member.VoiceSeconds }; }
}
