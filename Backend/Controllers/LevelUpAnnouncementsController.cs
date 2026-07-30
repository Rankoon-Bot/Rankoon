using Discord.WebSocket;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Rankoon.Api;
using Rankoon.Data.Auth;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;
using Rankoon.Data.Xp;

namespace Rankoon.Controllers;

public sealed record LevelUpPreviewRequest(LevelProgressScope Scope, LevelAnnouncementKind Kind, LevelAnnouncementGroup? Group, LevelAnnouncementMessage? Message, string? DisplayName, string? Username, int Level, int? PreviousLevel, decimal? TotalXp, decimal? GainedXp, string? Source, bool RewardRoleAwarded, string? SeasonName = null);

[ApiController, Authorize, Route("api/guilds/{guildId}/xp/level-up-announcements")]
public sealed class LevelUpAnnouncementsController(IGuildAuthorizationService authorization, IGuildDiscordContextResolver discord, RankoonDbContext database, ILevelUpTemplateRenderer renderer, LevelUpTemplateSelector selector, IDiscordAnnouncementSender sender, IReportWriter reports, TimeProvider timeProvider) : ControllerBase
{
    private async Task<(ulong Id, IActionResult? Error)> AuthorizeAsync(string guildId) => !ulong.TryParse(guildId, out var id) ? (0, this.ApiError("guild.invalidId")) : await authorization.CanAccessModuleAsync(User, id, GuildModuleIds.XpAnnouncements, HttpContext.RequestAborted) ? (id, null) : (0, Forbid());

    [HttpGet] public async Task<IActionResult> Get(string guildId)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        var settings = await GetOrMigrateAsync(id); var guild = (await discord.ResolveAsync(id, HttpContext.RequestAborted))?.Guild;
        return Ok(new { settings, migrated = settings.SchemaVersion == 2 && settings.Revision == 0 && settings.Lifetime.LevelUp.Groups.Count > 0, channelStatus = new { lifetime = ChannelStatus(guild, settings.Lifetime.ChannelId), season = ChannelStatus(guild, settings.Season.ChannelId) } });
    }

    [HttpPut] public async Task<IActionResult> Save(string guildId, [FromBody] GuildLevelUpAnnouncementSettings settings)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        var validation = Validate(settings); var guild = (await discord.ResolveAsync(id, HttpContext.RequestAborted))?.Guild;
        ValidateChannel(validation, "lifetime.channelId", settings.Lifetime, guild); ValidateChannel(validation, "season.channelId", settings.Season, guild);
        if (validation.Count > 0) return this.ApiError("levelAnnouncements.settingsInvalid", errors: validation.GroupBy(x => x.Field).ToDictionary(x => x.Key, x => (IReadOnlyList<ApiValidationError>)x.Select(e => ApiErrorFactory.Validation(e.Code)).ToArray()));
        settings.Id = null; settings.GuildId = id; settings.SchemaVersion = 2; settings.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var replacement = Builders<GuildLevelUpAnnouncementSettings>.Update.Set(x => x.SchemaVersion, 2).Set(x => x.Lifetime, settings.Lifetime).Set(x => x.Season, settings.Season).Set(x => x.UpdatedAtUtc, settings.UpdatedAtUtc).Unset("enabled").Unset("channel_id").Unset("notify_mentioned_user").Unset("use_default_fallback").Unset("fallback_locale").Unset("announce_manual_adjustments").Unset("avoid_recent_templates_per_user").Unset("templates").Inc(x => x.Revision, 1);
        var saved = await database.GuildLevelUpAnnouncementSettings.FindOneAndUpdateAsync(x => x.GuildId == id && x.Revision == settings.Revision, replacement, new FindOneAndUpdateOptions<GuildLevelUpAnnouncementSettings> { ReturnDocument = ReturnDocument.After }, HttpContext.RequestAborted);
        if (saved == null) { var exists = await database.GuildLevelUpAnnouncementSettings.Find(x => x.GuildId == id).AnyAsync(HttpContext.RequestAborted); if (exists || settings.Revision != 0) return this.ApiError("levelAnnouncements.revisionConflict"); settings.Revision = 1; await database.GuildLevelUpAnnouncementSettings.InsertOneAsync(settings, cancellationToken: HttpContext.RequestAborted); saved = settings; }
        await reports.WriteAsync(new(id, ReportCategories.Activity, ReportNames.LevelAnnouncementSettingsChanged, ReportOutcomes.Succeeded, ActorId: authorization.GetDiscordUserId(User)), HttpContext.RequestAborted);
        return Ok(saved);
    }

    [HttpGet("template-schema")] public async Task<IActionResult> Schema(string guildId)
    {
        var (_, error) = await AuthorizeAsync(guildId); return error ?? Ok(new { maximumTemplateLength = 500, maximumRenderedLength = 2000, tokens = renderer.Tokens.Select(name => new { name, requiresRewardRole = name.StartsWith("rewardRole", StringComparison.Ordinal), requiresSeason = name.StartsWith("season.", StringComparison.Ordinal) }) });
    }

    [HttpPost("preview")] public async Task<IActionResult> Preview(string guildId, [FromBody] LevelUpPreviewRequest request)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        var message = request.Message ?? request.Group?.Messages.FirstOrDefault(); if (message == null) return this.ApiError("levelAnnouncements.templateInvalid");
        var validation = renderer.Validate(message, request.Scope, request.Kind); if (validation.Count > 0) return Ok(new { content = (string?)null, tokens = Array.Empty<string>(), validationErrors = validation });
        var guild = (await discord.ResolveAsync(id, HttpContext.RequestAborted))?.Guild; var context = Context(request, authorization.GetDiscordUserId(User), guild);
        var result = renderer.Render(message.Content, context); return Ok(new { content = result.Content, tokens = result.Tokens, validationErrors = result.Errors });
    }

    [HttpPost("test")] public async Task<IActionResult> Test(string guildId, [FromBody] LevelUpPreviewRequest request)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        var settings = await GetOrMigrateAsync(id); var profile = request.Scope == LevelProgressScope.Season ? settings.Season : settings.Lifetime; var guild = (await discord.ResolveAsync(id, HttpContext.RequestAborted))?.Guild; var channel = profile.ChannelId is { } channelId ? guild?.GetTextChannel(channelId) : null;
        if (!profile.Enabled || channel == null) return this.ApiError("levelAnnouncements.channelUnavailable");
        var context = Context(request, authorization.GetDiscordUserId(User), guild); var selection = request.Message is { } message ? new LevelUpTemplateSelection(request.Kind, request.Group ?? new LevelAnnouncementGroup { Name = "Test", Messages = [message] }, message) : selector.Select(profile, request.Kind, context, [], []);
        if (selection == null && request.Kind == LevelAnnouncementKind.Reward) selection = selector.Select(profile, LevelAnnouncementKind.LevelUp, context, [], []);
        var content = selection == null ? (profile.UseDefaultFallback ? LevelAnnouncementFallbacks.Content(profile.FallbackLocale, request.Kind == LevelAnnouncementKind.Reward) : null) : selection.Message.Content;
        var result = content == null ? new TemplateRenderResult(null, [], []) : renderer.Render(content, context); if (result.Content == null || result.Content.Length > 2000) return this.ApiError("levelAnnouncements.templateInvalid");
        var messageId = await sender.SendAsync(channel, "[TEST]\n\n" + result.Content, authorization.GetDiscordUserId(User) ?? 0, false, HttpContext.RequestAborted);
        await reports.WriteAsync(new(id, ReportCategories.Activity, ReportNames.LevelAnnouncementTestSent, ReportOutcomes.Succeeded, ActorId: authorization.GetDiscordUserId(User), ChannelId: channel.Id), HttpContext.RequestAborted);
        return Ok(new { messageId, selectedGroupId = selection?.Group.Id, selectedMessageId = selection?.Message.Id });
    }

    private async Task<GuildLevelUpAnnouncementSettings> GetOrMigrateAsync(ulong guildId)
    {
        var existing = await database.GuildLevelUpAnnouncementSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (existing != null) return existing.SchemaVersion >= 2 ? existing : Migrate(existing);
        var legacy = await database.GuildXpSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(HttpContext.RequestAborted);
        return new GuildLevelUpAnnouncementSettings { GuildId = guildId, Lifetime = new LevelAnnouncementProfile { ChannelId = legacy?.LevelUpChannelId, LevelUp = new LevelAnnouncementSet { Groups = [new() { Name = "Default", Messages = [new() { Content = "Congratulations {user.mention}! You reached level {level}." }] }] } } };
    }
    internal static GuildLevelUpAnnouncementSettings Migrate(GuildLevelUpAnnouncementSettings old)
    {
        var lifetime = new LevelAnnouncementProfile { Enabled = old.LegacyEnabled, ChannelId = old.LegacyChannelId, NotifyUser = old.LegacyNotifyMentionedUser, UseDefaultFallback = old.LegacyUseDefaultFallback, FallbackLocale = old.LegacyFallbackLocale ?? "en", AnnounceManualAdjustments = old.LegacyAnnounceManualAdjustments, AvoidRecentMessagesPerUser = old.LegacyAvoidRecentTemplatesPerUser };
        foreach (var template in old.LegacyTemplates)
        {
            var group = new LevelAnnouncementGroup { Name = string.IsNullOrWhiteSpace(template.Name) ? "Migrated" : template.Name, Enabled = template.Enabled, Weight = Math.Max(1, template.Weight), Conditions = new() { MinimumLevel = template.MinimumLevel, MaximumLevel = template.MaximumLevel, EveryNthLevel = template.EveryNthLevel, ExactLevels = template.ExactLevels, Sources = template.Sources }, Messages = template.EffectiveContents.Select(content => new LevelAnnouncementMessage { Content = content }).ToList() };
            if (template.RewardRoleRequirement is RewardRoleRequirement.Required or RewardRoleRequirement.Any) lifetime.Rewards.Groups.Add(group);
            if (template.RewardRoleRequirement is RewardRoleRequirement.NotAwarded or RewardRoleRequirement.Any) lifetime.LevelUp.Groups.Add(template.RewardRoleRequirement == RewardRoleRequirement.Any ? new LevelAnnouncementGroup { Name = group.Name, Enabled = group.Enabled, Weight = group.Weight, Conditions = group.Conditions, Messages = group.Messages.Select(x => new LevelAnnouncementMessage { Content = x.Content }).ToList() } : group);
        }
        return new GuildLevelUpAnnouncementSettings { Id = old.Id, GuildId = old.GuildId, SchemaVersion = 2, Lifetime = lifetime, Season = new(), Revision = old.Revision, UpdatedAtUtc = old.UpdatedAtUtc };
    }
    private List<TemplateValidationError> Validate(GuildLevelUpAnnouncementSettings settings)
    {
        var errors = new List<TemplateValidationError>(); ValidateProfile(errors, settings.Lifetime, LevelProgressScope.Lifetime, "lifetime"); ValidateProfile(errors, settings.Season, LevelProgressScope.Season, "season");
        var allIds = settings.Lifetime.LevelUp.Groups.Concat(settings.Lifetime.Rewards.Groups).Concat(settings.Season.LevelUp.Groups).Concat(settings.Season.Rewards.Groups).SelectMany(x => x.Messages.Select(m => m.Id).Append(x.Id));
        if (allIds.Distinct(StringComparer.Ordinal).Count() != allIds.Count()) errors.Add(new("settings", "duplicateId")); return errors;
    }
    private void ValidateProfile(List<TemplateValidationError> errors, LevelAnnouncementProfile profile, LevelProgressScope scope, string prefix)
    {
        if (profile.AvoidRecentMessagesPerUser is < 0 or > 20) errors.Add(new($"{prefix}.avoidRecentMessagesPerUser", "invalid"));
        foreach (var (set, kind, key) in new[] { (profile.LevelUp, LevelAnnouncementKind.LevelUp, "levelUp"), (profile.Rewards, LevelAnnouncementKind.Reward, "rewards") }) foreach (var (group, index) in set.Groups.Select((g, i) => (g, i)))
        {
            var path = $"{prefix}.{key}.groups[{index}]"; if (string.IsNullOrWhiteSpace(group.Name)) errors.Add(new(path + ".name", "required")); if (group.Weight is < 1 or > 10000) errors.Add(new(path + ".weight", "invalid"));
            if (group.Messages.Count == 0) errors.Add(new(path + ".messages", "required")); if (group.Enabled && !group.Messages.Any(x => x.Enabled)) errors.Add(new(path + ".messages", "activeMessageRequired"));
            if (group.Conditions.MinimumLevel is < 1 || group.Conditions.MaximumLevel is < 1 || group.Conditions.MinimumLevel > group.Conditions.MaximumLevel || group.Conditions.EveryNthLevel is <= 0 || group.Conditions.ExactLevels.Any(x => x < 1)) errors.Add(new(path + ".conditions", "invalid"));
            foreach (var (message, messageIndex) in group.Messages.Select((m, i) => (m, i))) errors.AddRange(renderer.Validate(message, scope, kind).Select(x => x with { Field = path + $".messages[{messageIndex}]." + x.Field }));
        }
    }
    private static void ValidateChannel(List<TemplateValidationError> errors, string field, LevelAnnouncementProfile profile, SocketGuild? guild) { if (profile.Enabled && !profile.ChannelId.HasValue) errors.Add(new(field, "required")); else if (profile.ChannelId.HasValue && guild?.GetTextChannel(profile.ChannelId.Value) == null) errors.Add(new(field, "channelInvalid")); }
    private static object ChannelStatus(SocketGuild? guild, ulong? channel) => new { exists = channel.HasValue && guild?.GetTextChannel(channel.Value) != null, canSend = channel.HasValue && guild?.GetTextChannel(channel.Value) != null };
    private static LevelUpRenderContext Context(LevelUpPreviewRequest request, ulong? currentUserId, SocketGuild? guild)
    {
        var level = Math.Max(1, request.Level); var previous = request.PreviousLevel ?? Math.Max(1, level - 1); var user = currentUserId.HasValue ? guild?.GetUser(currentUserId.Value) : null; var userId = user?.Id ?? currentUserId ?? 123UL;
        return new($"<@{userId}>", request.DisplayName ?? user?.DisplayName ?? "User", request.Username ?? user?.Username ?? "user", userId, previous, level, 0, request.TotalXp ?? 1000, request.GainedXp ?? 100, request.Source ?? "message", "<#123>", guild?.Name ?? "Guild", guild?.MemberCount ?? 0, 42, 3600, 1, request.RewardRoleAwarded ? [new LevelRoleChange(123, "Reward", level)] : []) { Scope = request.Scope, SeasonId = request.Scope == LevelProgressScope.Season ? "example" : null, SeasonName = request.SeasonName ?? (request.Scope == LevelProgressScope.Season ? "Season" : null), SeasonNumber = request.Scope == LevelProgressScope.Season ? 1 : null, SeasonStartsAtUtc = request.Scope == LevelProgressScope.Season ? DateTime.UtcNow.AddDays(-5) : null, SeasonEndsAtUtc = request.Scope == LevelProgressScope.Season ? DateTime.UtcNow.AddDays(5) : null, SeasonLeaderboardRank = request.Scope == LevelProgressScope.Season ? 1 : null };
    }
}
