using System.Text;
using System.Text.RegularExpressions;
using Rankoon.Data.Model;

namespace Rankoon.Data.Xp;

public sealed record LevelUpRenderContext(string UserMention, string DisplayName, string Username, ulong UserId, int PreviousLevel, int Level, decimal PreviousXp, decimal TotalXp, decimal GainedXp, string Source, string? SourceChannelMention, string GuildName, int GuildMemberCount, long Messages, long VoiceSeconds, long? LeaderboardRank, IReadOnlyList<LevelRoleChange> RewardRoles)
{
    public int LevelsGained => Level - PreviousLevel;
    public decimal NextLevelXp => Mee6LevelCurve.RequiredXpForLevel(Level + 1);
    public decimal RemainingXp => Math.Max(0, NextLevelXp - TotalXp);
}

public sealed record TemplateValidationError(string Field, string Code);
public sealed record TemplateRenderResult(string? Content, IReadOnlyList<string> Tokens, IReadOnlyList<TemplateValidationError> Errors);

public interface ILevelUpTemplateRenderer
{
    TemplateRenderResult Render(string content, LevelUpRenderContext context, bool validateContext = true);
    IReadOnlyList<TemplateValidationError> Validate(LevelUpMessageTemplate template);
    IReadOnlyList<string> Tokens { get; }
}

public sealed class LevelUpTemplateRenderer : ILevelUpTemplateRenderer
{
    private static readonly Regex TokenPattern = new("(?<!\\{)\\{([a-zA-Z][a-zA-Z0-9.]*)\\}(?!\\})", RegexOptions.Compiled);
    private static readonly HashSet<string> Supported = ["user.mention", "user.displayName", "user.username", "user.id", "level", "previousLevel", "levelsGained", "xp.total", "xp.gained", "xp.nextLevel", "xp.remaining", "rewardRole.name", "rewardRole.mention", "rewardRoles.names", "rewardRoles.mentions", "leaderboard.rank", "stats.messages", "stats.voiceTime", "source", "sourceChannel.mention", "guild.name", "guild.memberCount"];
    public IReadOnlyList<string> Tokens => Supported.Order(StringComparer.Ordinal).ToArray();

    public IReadOnlyList<TemplateValidationError> Validate(LevelUpMessageTemplate template)
    {
        var errors = new List<TemplateValidationError>();
        if (string.IsNullOrWhiteSpace(template.Id)) errors.Add(new("id", "required"));
        if (string.IsNullOrWhiteSpace(template.Name)) errors.Add(new("name", "required"));
        if (template.EffectiveContents.Count == 0 || template.EffectiveContents.Any(content => string.IsNullOrWhiteSpace(content) || content.Length > 500)) errors.Add(new("contents", "length"));
        if (template.Weight < 1) errors.Add(new("weight", "invalid"));
        if (template.MinimumLevel is < 1 || template.MaximumLevel is < 1 || template.MinimumLevel > template.MaximumLevel) errors.Add(new("levels", "invalid"));
        if (template.EveryNthLevel is <= 0 || template.ExactLevels.Any(level => level < 1)) errors.Add(new("conditions", "invalid"));
        var rendered = template.EffectiveContents.Select(content => Render(content, EmptyContext, false)).ToArray();
        errors.AddRange(rendered.SelectMany(result => result.Errors));
        var roleToken = rendered.SelectMany(result => result.Tokens).Any(token => token.StartsWith("rewardRole", StringComparison.Ordinal));
        if (roleToken && template.RewardRoleRequirement != RewardRoleRequirement.Required) errors.Add(new("rewardRoleRequirement", "requiredForRoleTokens"));
        return errors;
    }

    public TemplateRenderResult Render(string content, LevelUpRenderContext context, bool validateContext = true)
    {
        var errors = new List<TemplateValidationError>();
        var tokens = new List<string>();
        var sentinelOpen = "\u0001"; var sentinelClose = "\u0002";
        var escaped = content.Replace("{{", sentinelOpen, StringComparison.Ordinal).Replace("}}", sentinelClose, StringComparison.Ordinal);
        var result = TokenPattern.Replace(escaped, match =>
        {
            var token = match.Groups[1].Value; tokens.Add(token);
            if (!Supported.Contains(token)) { errors.Add(new("content", "unknownToken")); return match.Value; }
            var value = Value(token, context);
            if (value == null && validateContext) { errors.Add(new("content", "missingContext")); return match.Value; }
            return value ?? string.Empty;
        });
        // Valid tokens have been replaced above. Any braces left here are malformed syntax;
        // adjacent tokens such as {user.id}{level} deliberately leave no separator.
        if (result.Contains('{') || result.Contains('}')) errors.Add(new("content", "invalidToken"));
        result = result.Replace(sentinelOpen, "{", StringComparison.Ordinal).Replace(sentinelClose, "}", StringComparison.Ordinal);
        return errors.Count == 0 ? new(result, tokens.Distinct(StringComparer.Ordinal).ToArray(), errors) : new(null, tokens.Distinct(StringComparer.Ordinal).ToArray(), errors);
    }

    private static string? Value(string token, LevelUpRenderContext c) => token switch
    {
        "user.mention" => c.UserMention, "user.displayName" => Escape(c.DisplayName), "user.username" => Escape(c.Username), "user.id" => c.UserId.ToString(),
        "level" => c.Level.ToString(), "previousLevel" => c.PreviousLevel.ToString(), "levelsGained" => c.LevelsGained.ToString(),
        "xp.total" => c.TotalXp.ToString("0"), "xp.gained" => c.GainedXp.ToString("0"), "xp.nextLevel" => c.NextLevelXp.ToString("0"), "xp.remaining" => c.RemainingXp.ToString("0"),
        "rewardRole.name" => c.RewardRoles.LastOrDefault()?.Name is { } name ? Escape(name) : null,
        "rewardRole.mention" => c.RewardRoles.LastOrDefault() is { } role ? $"<@&{role.RoleId}>" : null,
        "rewardRoles.names" => c.RewardRoles.Count == 0 ? null : string.Join(", ", c.RewardRoles.Select(x => Escape(x.Name))),
        "rewardRoles.mentions" => c.RewardRoles.Count == 0 ? null : string.Join(" ", c.RewardRoles.Select(x => $"<@&{x.RoleId}>")),
        "leaderboard.rank" => c.LeaderboardRank?.ToString(), "stats.messages" => c.Messages.ToString(), "stats.voiceTime" => TimeSpan.FromSeconds(c.VoiceSeconds).ToString("g"),
        "source" => Escape(c.Source), "sourceChannel.mention" => c.SourceChannelMention,
        "guild.name" => Escape(c.GuildName), "guild.memberCount" => c.GuildMemberCount.ToString(), _ => null
    };
    private static string Escape(string value) => value.Replace("@", "@\u200b", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    private static readonly LevelUpRenderContext EmptyContext = new("@user", "User", "user", 1, 1, 2, 0, 100, 100, "message", "#general", "Guild", 1, 0, 0, 1, [new LevelRoleChange(1, "Role", 1)]);
}

public interface ILevelUpRandom { int Next(int maximumExclusive); }
public sealed class LevelUpRandom : ILevelUpRandom { public int Next(int maximumExclusive) => Random.Shared.Next(maximumExclusive); }
public sealed record LevelUpTemplateSelection(LevelUpMessageTemplate Template, string Content);

public sealed class LevelUpTemplateSelector(ILevelUpTemplateRenderer renderer, ILevelUpRandom random)
{
    public LevelUpTemplateSelection? Select(GuildLevelUpAnnouncementSettings settings, LevelUpRenderContext context, IReadOnlyCollection<string> recent)
    {
        var candidates = settings.Templates.Where(t => t.Enabled && (t.MinimumLevel == null || context.Level >= t.MinimumLevel) && (t.MaximumLevel == null || context.Level <= t.MaximumLevel)
            && (t.ExactLevels.Count == 0 || t.ExactLevels.Contains(context.Level)) && (t.EveryNthLevel == null || context.Level % t.EveryNthLevel == 0)
            && (t.Sources.Count == 0 || t.Sources.Contains(context.Source, StringComparer.OrdinalIgnoreCase))
            && (t.RewardRoleRequirement != RewardRoleRequirement.Required || context.RewardRoles.Count > 0)
            && (t.RewardRoleRequirement != RewardRoleRequirement.NotAwarded || context.RewardRoles.Count == 0)
            && t.EffectiveContents.Any(content => renderer.Render(content, context).Errors.Count == 0)).ToList();
        if (candidates.Count == 0) return null;
        candidates = candidates.Where(t => t.Priority == candidates.Max(x => x.Priority)).ToList();
        var nonRecent = candidates.Where(t => !recent.Contains(t.Id)).ToList();
        if (nonRecent.Count > 0) candidates = nonRecent;
        var total = candidates.Sum(t => t.Weight); var value = random.Next(total);
        var template = candidates[^1];
        foreach (var candidate in candidates) { value -= candidate.Weight; if (value < 0) { template = candidate; break; } }
        var contents = template.EffectiveContents.Where(content => renderer.Render(content, context).Errors.Count == 0).ToArray();
        return new(template, contents[random.Next(contents.Length)]);
    }
}
