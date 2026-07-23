using Discord.WebSocket;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using System.Text.RegularExpressions;
using Rankoon.Api;
using Rankoon.Data.Auth;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;
using Rankoon.Data.Xp;

namespace Rankoon.Controllers;

public sealed record LevelUpPreviewRequest(LevelUpMessageTemplate Template, string? DisplayName, string? Username, int Level, int? PreviousLevel, decimal? TotalXp, decimal? GainedXp, string? Source, bool RewardRoleAwarded, int VariationIndex = 0);

[ApiController, Authorize, Route("api/guilds/{guildId}/xp/level-up-announcements")]
public sealed class LevelUpAnnouncementsController(IGuildAuthorizationService authorization, DiscordShardedClient discord, RankoonDbContext database, ILevelUpTemplateRenderer renderer, LevelUpTemplateSelector selector, IDiscordAnnouncementSender sender, IReportWriter reports) : ControllerBase
{
    private static readonly Regex UserMentionPattern = new("<@!?(?<id>\\d+)>", RegexOptions.Compiled);
    private async Task<(ulong Id, IActionResult? Error)> AuthorizeAsync(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id)) return (0, this.ApiError("guild.invalidId"));
        return await authorization.CanAccessModuleAsync(User, id, GuildModuleIds.XpAnnouncements, HttpContext.RequestAborted) ? (id, null) : (0, Forbid());
    }

    [HttpGet]
    public async Task<IActionResult> Get(string guildId)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        var settings = await GetOrMigrateAsync(id); var channel = settings.ChannelId.HasValue ? discord.GetGuild(id)?.GetTextChannel(settings.ChannelId.Value) : null;
        return Ok(new { settings, legacyChannelMigrated = settings.ChannelId.HasValue && settings.Revision == 0, channelStatus = new { exists = channel != null, canSend = channel != null } });
    }

    [HttpPut]
    public async Task<IActionResult> Save(string guildId, [FromBody] GuildLevelUpAnnouncementSettings settings)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        var errors = Validate(settings);
        if (settings.ChannelId.HasValue && discord.GetGuild(id)?.GetTextChannel(settings.ChannelId.Value) == null) errors.Add(new("channelId", "channelInvalid"));
        if (errors.Count > 0) return this.ApiError("levelAnnouncements.settingsInvalid", errors: errors.GroupBy(x => x.Field).ToDictionary(x => x.Key, x => (IReadOnlyList<ApiValidationError>)x.Select(e => ApiErrorFactory.Validation(e.Code)).ToArray()));
        settings.GuildId = id; settings.UpdatedAtUtc = DateTime.UtcNow;
        var update = Builders<GuildLevelUpAnnouncementSettings>.Update.Set(x => x.Enabled, settings.Enabled).Set(x => x.ChannelId, settings.ChannelId).Set(x => x.NotifyMentionedUser, settings.NotifyMentionedUser).Set(x => x.UseDefaultFallback, settings.UseDefaultFallback).Set(x => x.FallbackLocale, settings.FallbackLocale).Set(x => x.AnnounceManualAdjustments, settings.AnnounceManualAdjustments).Set(x => x.AvoidRecentTemplatesPerUser, settings.AvoidRecentTemplatesPerUser).Set(x => x.Templates, settings.Templates).Set(x => x.UpdatedAtUtc, settings.UpdatedAtUtc).Inc(x => x.Revision, 1);
        var saved = await database.GuildLevelUpAnnouncementSettings.FindOneAndUpdateAsync(x => x.GuildId == id && x.Revision == settings.Revision, update, new FindOneAndUpdateOptions<GuildLevelUpAnnouncementSettings> { ReturnDocument = ReturnDocument.After }, HttpContext.RequestAborted);
        if (saved == null)
        {
            var existing = await database.GuildLevelUpAnnouncementSettings.Find(x => x.GuildId == id).FirstOrDefaultAsync(HttpContext.RequestAborted);
            if (existing == null && settings.Revision == 0) { settings.Revision = 1; await database.GuildLevelUpAnnouncementSettings.InsertOneAsync(settings, cancellationToken: HttpContext.RequestAborted); saved = settings; }
            else return this.ApiError("levelAnnouncements.revisionConflict");
        }
        await reports.WriteAsync(new(id, ReportCategories.Activity, ReportNames.LevelAnnouncementSettingsChanged, ReportOutcomes.Succeeded, ActorId: authorization.GetDiscordUserId(User)), HttpContext.RequestAborted);
        return Ok(saved);
    }

    [HttpGet("template-schema")]
    public async Task<IActionResult> Schema(string guildId)
    {
        var (_, error) = await AuthorizeAsync(guildId); return error ?? Ok(new { maximumTemplateLength = 500, maximumRenderedLength = 2000, tokens = renderer.Tokens.Select(token => new { name = token, requiresRewardRole = token.StartsWith("rewardRole", StringComparison.Ordinal) }) });
    }

    [HttpPost("preview")]
    public async Task<IActionResult> Preview(string guildId, [FromBody] LevelUpPreviewRequest request)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        var validation = renderer.Validate(request.Template); if (validation.Count > 0) return Ok(new { content = (string?)null, tokens = Array.Empty<string>(), validationErrors = validation, userMentions = Array.Empty<object>() });
        var guild = discord.GetGuild(id); var level = Math.Max(1, request.Level); var previous = request.PreviousLevel ?? Math.Max(1, level - 1);
        var context = Context(request, authorization.GetDiscordUserId(User), guild, level, previous);
        var content = request.Template.EffectiveContents.ElementAtOrDefault(Math.Max(0, request.VariationIndex)) ?? request.Template.EffectiveContents.FirstOrDefault() ?? string.Empty;
        var result = renderer.Render(content, context);
        return Ok(new { content = result.Content, tokens = result.Tokens, validationErrors = result.Errors, userMentions = ResolveUserMentions(guild, result.Content) });
    }

    [HttpPost("test")]
    public async Task<IActionResult> Test(string guildId, [FromBody] LevelUpPreviewRequest request)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        var settings = await GetOrMigrateAsync(id); var channel = settings.ChannelId.HasValue ? discord.GetGuild(id)?.GetTextChannel(settings.ChannelId.Value) : null;
        if (channel == null) return this.ApiError("levelAnnouncements.channelUnavailable");
        var guild = discord.GetGuild(id)!; var context = Context(request, authorization.GetDiscordUserId(User), guild, Math.Max(1, request.Level), request.PreviousLevel ?? Math.Max(1, request.Level - 1));
        // Test the currently edited template, but use the same selector as the worker for its variants.
        var testSettings = new GuildLevelUpAnnouncementSettings { Templates = [request.Template] };
        var selection = selector.Select(testSettings, context, []);
        if (selection == null) return this.ApiError("levelAnnouncements.templateInvalid");
        var result = renderer.Render(selection.Content, context);
        if (result.Content == null || result.Content.Length > 2000) return this.ApiError("levelAnnouncements.templateInvalid");
        var messageId = await sender.SendAsync(channel, "[TEST]\n\n" + result.Content, false, HttpContext.RequestAborted);
        await reports.WriteAsync(new(id, ReportCategories.Activity, ReportNames.LevelAnnouncementTestSent, ReportOutcomes.Succeeded, ActorId: authorization.GetDiscordUserId(User), ChannelId: channel.Id), HttpContext.RequestAborted);
        return Ok(new { messageId, selectedTemplateId = selection.Template.Id });
    }

    private async Task<GuildLevelUpAnnouncementSettings> GetOrMigrateAsync(ulong guildId)
    {
        var existing = await database.GuildLevelUpAnnouncementSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(HttpContext.RequestAborted); if (existing != null) return existing;
        var legacy = await database.GuildXpSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(HttpContext.RequestAborted);
        return new GuildLevelUpAnnouncementSettings { GuildId = guildId, ChannelId = legacy?.LevelUpChannelId, Templates = Defaults() };
    }
    private List<TemplateValidationError> Validate(GuildLevelUpAnnouncementSettings settings)
    {
        var errors = new List<TemplateValidationError>(); if (settings.AvoidRecentTemplatesPerUser is < 0 or > 20) errors.Add(new("avoidRecentTemplatesPerUser", "invalid"));
        if (settings.Templates.Select(x => x.Id).Distinct().Count() != settings.Templates.Count) errors.Add(new("templates", "duplicateId"));
        errors.AddRange(settings.Templates.SelectMany(renderer.Validate)); return errors;
    }
    private static LevelUpRenderContext Context(LevelUpPreviewRequest r, ulong? currentUserId, SocketGuild? guild, int level, int previous)
    {
        var user = currentUserId.HasValue ? guild?.GetUser(currentUserId.Value) : null;
        var userId = user?.Id ?? currentUserId ?? 123UL;
        return new($"<@{userId}>", r.DisplayName ?? user?.DisplayName ?? "User", r.Username ?? user?.Username ?? "user", userId, previous, level, 0, r.TotalXp ?? 1000, r.GainedXp ?? 100, r.Source ?? "message", "<#123>", guild?.Name ?? "Guild", guild?.MemberCount ?? 0, 42, 3600, 1, r.RewardRoleAwarded ? [new LevelRoleChange(123, "Profi", level)] : []);
    }
    private static List<LevelUpMessageTemplate> Defaults() => [new() { Name = "Level up", Contents = ["Level {level} fuer {user.mention}? Das ist richtig stark!", "GG {user.mention}! Level {level} ist geschafft!"], Weight = 1 }, new() { Name = "Reward", Contents = ["{user.mention} hat Level {level} erreicht und die Rolle {rewardRole.mention} erhalten!"], Priority = 50, RewardRoleRequirement = RewardRoleRequirement.Required }, new() { Name = "Multi level", Contents = ["{user.mention} ist direkt um {levelsGained} Level auf Level {level} gesprungen!"], Priority = 20, MinimumLevel = 2 }];

    private static IReadOnlyList<object> ResolveUserMentions(SocketGuild? guild, string? content) => content == null || guild == null ? [] : UserMentionPattern.Matches(content)
        .Select(match => ulong.TryParse(match.Groups["id"].Value, out var id) ? guild.GetUser(id) : null)
        .Where(user => user != null).DistinctBy(user => user!.Id)
        .Select(user => (object)new { id = user!.Id.ToString(), displayName = user.DisplayName, username = user.Username }).ToArray();
}
