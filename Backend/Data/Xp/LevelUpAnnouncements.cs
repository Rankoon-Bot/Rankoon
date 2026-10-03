using System.Text.RegularExpressions;
using Rankoon.Data.Model;

namespace Rankoon.Data.Xp;

public sealed record LevelUpRenderContext(string UserMention, string DisplayName, string Username, ulong UserId, int PreviousLevel, int Level, decimal PreviousXp, decimal TotalXp, decimal GainedXp, string Source, string? SourceChannelMention, string GuildName, int GuildMemberCount, long Messages, decimal VoiceSeconds, long? LeaderboardRank, IReadOnlyList<LevelRoleChange> RewardRoles)
{
    public LevelProgressScope Scope { get; init; } = LevelProgressScope.Lifetime;
    public string? SeasonId { get; init; }
    public string? SeasonName { get; init; }
    public long? SeasonNumber { get; init; }
    public DateTime? SeasonStartsAtUtc { get; init; }
    public DateTime? SeasonEndsAtUtc { get; init; }
    public long? SeasonLeaderboardRank { get; init; }
    public int LevelsGained => Level - PreviousLevel;
    public decimal NextLevelXp => Mee6LevelCurve.RequiredXpForLevel(Level + 1);
    public decimal RemainingXp => Math.Max(0, NextLevelXp - TotalXp);
}

public sealed record TemplateValidationError(string Field, string Code);
public sealed record TemplateRenderResult(string? Content, IReadOnlyList<string> Tokens, IReadOnlyList<TemplateValidationError> Errors);
public interface ILevelUpTemplateRenderer
{
    TemplateRenderResult Render(string content, LevelUpRenderContext context, bool validateContext = true);
    IReadOnlyList<TemplateValidationError> Validate(LevelAnnouncementMessage message, LevelProgressScope scope, LevelAnnouncementKind kind);
    IReadOnlyList<string> Tokens { get; }
}

public sealed class LevelUpTemplateRenderer : ILevelUpTemplateRenderer
{
    private static readonly Regex TokenPattern = new("(?<!\\{)\\{([a-zA-Z][a-zA-Z0-9.]*)\\}(?!\\})", RegexOptions.Compiled);
    private static readonly HashSet<string> RewardTokens = ["rewardRole.name", "rewardRole.mention", "rewardRoles.names", "rewardRoles.mentions"];
    private static readonly HashSet<string> SeasonTokens = ["season.name", "season.number", "season.startsAt", "season.endsAt", "season.daysRemaining", "season.leaderboardRank"];
    private static readonly HashSet<string> Supported = ["user.mention", "user.displayName", "user.username", "user.id", "level", "previousLevel", "levelsGained", "xp.total", "xp.gained", "xp.nextLevel", "xp.remaining", "rewardRole.name", "rewardRole.mention", "rewardRoles.names", "rewardRoles.mentions", "leaderboard.rank", "stats.messages", "stats.voiceTime", "source", "sourceChannel.mention", "guild.name", "guild.memberCount", "season.name", "season.number", "season.startsAt", "season.endsAt", "season.daysRemaining", "season.leaderboardRank"];
    public IReadOnlyList<string> Tokens => Supported.Order(StringComparer.Ordinal).ToArray();

    public IReadOnlyList<TemplateValidationError> Validate(LevelAnnouncementMessage message, LevelProgressScope scope, LevelAnnouncementKind kind)
    {
        var errors = new List<TemplateValidationError>();
        if (string.IsNullOrWhiteSpace(message.Id)) errors.Add(new("id", "required"));
        if (string.IsNullOrWhiteSpace(message.Content) || message.Content.Length > 500) errors.Add(new("content", "length"));
        var render = Render(message.Content, EmptyContext with { Scope = scope }, false);
        errors.AddRange(render.Errors);
        if (kind != LevelAnnouncementKind.Reward && render.Tokens.Any(RewardTokens.Contains)) errors.Add(new("content", "rewardTokenRequiresRewardSet"));
        if (scope != LevelProgressScope.Season && render.Tokens.Any(SeasonTokens.Contains)) errors.Add(new("content", "seasonTokenRequiresSeasonScope"));
        return errors;
    }
    public TemplateRenderResult Render(string content, LevelUpRenderContext context, bool validateContext = true)
    {
        var errors = new List<TemplateValidationError>(); var tokens = new List<string>();
        const string sentinelOpen = "\u0001", sentinelClose = "\u0002";
        var escaped = content.Replace("{{", sentinelOpen, StringComparison.Ordinal).Replace("}}", sentinelClose, StringComparison.Ordinal);
        var result = TokenPattern.Replace(escaped, match =>
        {
            var token = match.Groups[1].Value; tokens.Add(token);
            if (!Supported.Contains(token)) { errors.Add(new("content", "unknownToken")); return match.Value; }
            var value = Value(token, context);
            if (value == null && validateContext) { errors.Add(new("content", "missingContext")); return match.Value; }
            return value ?? string.Empty;
        });
        if (result.Contains('{') || result.Contains('}')) errors.Add(new("content", "invalidToken"));
        result = result.Replace(sentinelOpen, "{", StringComparison.Ordinal).Replace(sentinelClose, "}", StringComparison.Ordinal);
        return errors.Count == 0 ? new(result, tokens.Distinct(StringComparer.Ordinal).ToArray(), errors) : new(null, tokens.Distinct(StringComparer.Ordinal).ToArray(), errors);
    }
    private static string? Value(string token, LevelUpRenderContext c) => token switch
    {
        "user.mention" => c.UserMention, "user.displayName" => Escape(c.DisplayName), "user.username" => Escape(c.Username), "user.id" => c.UserId.ToString(),
        "level" => c.Level.ToString(), "previousLevel" => c.PreviousLevel.ToString(), "levelsGained" => c.LevelsGained.ToString(),
        "xp.total" => c.TotalXp.ToString("0"), "xp.gained" => c.GainedXp.ToString("0"), "xp.nextLevel" => c.NextLevelXp.ToString("0"), "xp.remaining" => c.RemainingXp.ToString("0"),
        "rewardRole.name" => c.RewardRoles.LastOrDefault()?.Name is { } name ? Escape(name) : null, "rewardRole.mention" => c.RewardRoles.LastOrDefault() is { } role ? $"<@&{role.RoleId}>" : null,
        "rewardRoles.names" => c.RewardRoles.Count == 0 ? null : string.Join(", ", c.RewardRoles.Select(x => Escape(x.Name))), "rewardRoles.mentions" => c.RewardRoles.Count == 0 ? null : string.Join(" ", c.RewardRoles.Select(x => $"<@&{x.RoleId}>")),
        "leaderboard.rank" => c.LeaderboardRank?.ToString(), "stats.messages" => c.Messages.ToString(), "stats.voiceTime" => TimeSpan.FromSeconds((double)c.VoiceSeconds).ToString("g"), "source" => Escape(c.Source), "sourceChannel.mention" => c.SourceChannelMention,
        "guild.name" => Escape(c.GuildName), "guild.memberCount" => c.GuildMemberCount.ToString(), "season.name" => c.SeasonName is null ? null : Escape(c.SeasonName), "season.number" => c.SeasonNumber?.ToString(),
        "season.startsAt" => c.SeasonStartsAtUtc?.ToString("u"), "season.endsAt" => c.SeasonEndsAtUtc?.ToString("u"), "season.daysRemaining" => c.SeasonEndsAtUtc is { } ends ? Math.Max(0, (ends - DateTime.UtcNow).Days).ToString() : null, "season.leaderboardRank" => c.SeasonLeaderboardRank?.ToString(), _ => null
    };
    private static string Escape(string value) => value.Replace("@", "@\u200b", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    private static readonly LevelUpRenderContext EmptyContext = new("@user", "User", "user", 1, 1, 2, 0, 100, 100, "message", "#general", "Guild", 1, 0, 0, 1, [new LevelRoleChange(1, "Role", 1)]) { SeasonId = "season", SeasonName = "Season", SeasonNumber = 1, SeasonStartsAtUtc = DateTime.UnixEpoch, SeasonEndsAtUtc = DateTime.UnixEpoch.AddDays(1), SeasonLeaderboardRank = 1 };
}

public interface ILevelUpRandom { int Next(int maximumExclusive); }
public sealed class LevelUpRandom : ILevelUpRandom { public int Next(int maximumExclusive) => Random.Shared.Next(maximumExclusive); }
public sealed record LevelUpTemplateSelection(LevelAnnouncementKind Kind, LevelAnnouncementGroup Group, LevelAnnouncementMessage Message);

public sealed class LevelUpTemplateSelector(ILevelUpTemplateRenderer renderer, ILevelUpRandom random)
{
    public LevelUpTemplateSelection? Select(LevelAnnouncementProfile profile, LevelAnnouncementKind kind, LevelUpRenderContext context, IReadOnlyCollection<string> recentGroups, IReadOnlyCollection<string> recentMessages)
    {
        var set = kind == LevelAnnouncementKind.Reward ? profile.Rewards : profile.LevelUp;
        var candidates = set.Groups.Where(group => group.Enabled && Matches(group.Conditions, context)).Select(group => new { Group = group, Messages = group.Messages.Where(message => message.Enabled && renderer.Render(message.Content, context).Errors.Count == 0).ToArray() }).Where(x => x.Messages.Length > 0).ToList();
        if (candidates.Count == 0) return null;
        var nonRecentGroups = candidates.Where(x => !recentGroups.Contains(x.Group.Id)).ToList();
        if (nonRecentGroups.Count > 0) candidates = nonRecentGroups;
        var total = candidates.Sum(x => x.Group.Weight); var value = random.Next(total); var chosen = candidates[^1];
        foreach (var candidate in candidates) { value -= candidate.Group.Weight; if (value < 0) { chosen = candidate; break; } }
        var messages = chosen.Messages.Where(x => !recentMessages.Contains(x.Id)).ToArray();
        if (messages.Length == 0) messages = chosen.Messages;
        return new(kind, chosen.Group, messages[random.Next(messages.Length)]);
    }
    private static bool Matches(LevelAnnouncementConditions c, LevelUpRenderContext context) =>
        (c.MinimumLevel == null || context.Level >= c.MinimumLevel) && (c.MaximumLevel == null || context.Level <= c.MaximumLevel) &&
        (c.ExactLevels.Count == 0 || c.ExactLevels.Contains(context.Level)) && (c.EveryNthLevel == null || context.Level % c.EveryNthLevel == 0) &&
        (c.Sources.Count == 0 || c.Sources.Contains(context.Source, StringComparer.OrdinalIgnoreCase));
}

public static class LevelAnnouncementFallbacks
{
    public static string Content(string locale, bool reward) => (locale.StartsWith("de", StringComparison.OrdinalIgnoreCase), reward) switch
    {
        (true, true) => "Glückwunsch {user.mention}! Du hast Level {level} erreicht und {rewardRoles.mentions} erhalten.",
        (true, false) => "Glückwunsch {user.mention}! Du hast Level {level} erreicht.",
        (false, true) => "Congratulations {user.mention}! You reached level {level} and received {rewardRoles.mentions}.",
        _ => "Congratulations {user.mention}! You reached level {level}."
    };
}
