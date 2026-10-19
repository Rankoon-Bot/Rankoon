using Rankoon.Data.Model;

namespace Rankoon.Data.Discord;

/// <summary>Shared exclusions and rates for non-voice gateway adapters.</summary>
internal static class ActivityXpPolicy
{
    internal static bool IsExcluded(GuildXpSettings settings, IEnumerable<ulong> roles, ulong? channelId = null, ulong? categoryId = null, ulong? parentChannelId = null) =>
        !settings.Enabled || roles.Any(settings.ExcludedRoleIds.Contains) ||
        channelId.HasValue && settings.ExcludedChannelIds.Contains(channelId.Value) ||
        parentChannelId.HasValue && settings.ExcludedChannelIds.Contains(parentChannelId.Value) ||
        categoryId.HasValue && settings.ExcludedCategoryIds.Contains(categoryId.Value);

    internal static decimal ChannelMultiplier(GuildXpSettings settings, ulong channelId, ulong? parentChannelId = null) =>
        settings.ChannelMultipliers.FirstOrDefault(x => x.ChannelId == channelId)?.Multiplier ??
        settings.ChannelMultipliers.FirstOrDefault(x => x.ChannelId == parentChannelId)?.Multiplier ?? 1m;

    internal static decimal MessagePoints(int length, MessageXpSettings settings)
    {
        if (length < settings.MinimumCharacters) return 0;
        var capped = Math.Clamp(length, settings.MinimumCharacters, Math.Max(settings.MinimumCharacters, settings.MaximumCharacters));
        var range = Math.Max(1, settings.MaximumCharacters - settings.MinimumCharacters);
        return decimal.Floor(settings.MinimumPoints + (decimal)(capped - settings.MinimumCharacters) / range * (settings.MaximumPoints - settings.MinimumPoints));
    }
}
