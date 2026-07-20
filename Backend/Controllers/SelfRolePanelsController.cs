using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Rankoon.Api;
using Rankoon.Data.Auth;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Controllers;

[ApiController]
[Authorize]
[Route("api/guilds/{guildId}/self-role-panels")]
public sealed class SelfRolePanelsController(IGuildAuthorizationService authorization, DiscordShardedClient discord, RankoonDbContext database, SelfRoleService selfRoles, ILogger<SelfRolePanelsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(string guildId)
    {
        var (id, error) = await AuthorizeAsync(guildId);
        return error ?? Ok(await database.SelfRolePanels.Find(x => x.GuildId == id).SortByDescending(x => x.UpdatedAt).ToListAsync(HttpContext.RequestAborted));
    }

    [HttpPost]
    public async Task<IActionResult> Create(string guildId, [FromBody] SelfRolePanel panel)
    {
        var (id, guild, error) = await AuthorizeGuildAsync(guildId);
        if (error != null) return error;
        try { return Ok(await selfRoles.CreateAsync(guild!, panel, HttpContext.RequestAborted)); }
        catch (SelfRoleValidationException exception) { return this.ApiError(exception.ErrorKey); }
    }

    [HttpPut("{panelId}")]
    public async Task<IActionResult> Update(string guildId, string panelId, [FromBody] SelfRolePanel panel)
    {
        var (_, guild, error) = await AuthorizeGuildAsync(guildId);
        if (error != null) return error;
        try
        {
            var saved = await selfRoles.UpdateAsync(guild!, panelId, panel, HttpContext.RequestAborted);
            return saved == null ? NotFound() : Ok(saved);
        }
        catch (SelfRoleValidationException exception) { return this.ApiError(exception.ErrorKey); }
    }

    [HttpDelete("{panelId}")]
    public async Task<IActionResult> Delete(string guildId, string panelId)
    {
        var (_, guild, error) = await AuthorizeGuildAsync(guildId);
        if (error != null) return error;
        return await selfRoles.DeleteAsync(guild!, panelId, HttpContext.RequestAborted) ? NoContent() : NotFound();
    }

    [HttpGet("/api/guilds/{guildId}/self-role-resources")]
    public async Task<IActionResult> Resources(string guildId)
    {
        var (_, guild, error) = await AuthorizeGuildAsync(guildId);
        if (error != null) return error;
        var roles = guild!.Roles.Where(role => !role.IsManaged && !role.IsEveryone && role.Position < guild.CurrentUser.Hierarchy)
            .OrderByDescending(role => role.Position).Select(role => new { id = role.Id, role.Name });
        var channels = guild.TextChannels
            .Where(channel => channel.GetChannelType() is Discord.ChannelType.Text or Discord.ChannelType.News)
            .OrderBy(channel => channel.Position)
            .Select(channel => new { id = channel.Id, channel.Name, type = "Text" });
        IReadOnlyCollection<Discord.GuildEmote> guildEmotes;
        try { guildEmotes = await guild.GetEmotesAsync(); }
        catch (global::Discord.Net.HttpException exception)
        {
            logger.LogWarning(exception, "Could not load guild emotes from Discord REST for guild {GuildId}; falling back to the gateway cache", guild.Id);
            guildEmotes = guild.Emotes;
        }
        var emojis = guildEmotes.OrderBy(emote => emote.Name).Select(emote => new { id = emote.Id, emote.Name, animated = emote.Animated, url = emote.Url, available = emote.IsAvailable });
        return Ok(new { roles, channels, emojis });
    }

    private async Task<(ulong Id, IActionResult? Error)> AuthorizeAsync(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id)) return (0, this.ApiError("guild.invalidId"));
        return await authorization.CanAccessModuleAsync(User, id, GuildModuleIds.SelfRoles, HttpContext.RequestAborted) ? (id, null) : (0, Forbid());
    }

    private async Task<(ulong Id, SocketGuild? Guild, IActionResult? Error)> AuthorizeGuildAsync(string guildId)
    {
        var (id, error) = await AuthorizeAsync(guildId);
        if (error != null) return (0, null, error);
        var guild = discord.GetGuild(id);
        return guild == null ? (0, null, NotFound()) : (id, guild, null);
    }
}
