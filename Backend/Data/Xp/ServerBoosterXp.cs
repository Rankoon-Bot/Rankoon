using Discord;
using Rankoon.Data.Model;

namespace Rankoon.Data.Xp;

public static class ServerBoosterXpSettingsValidator
{
    public const int MaximumTiers = 10;

    public static IReadOnlyList<(string Field, string ErrorKey)> Validate(ServerBoosterXpSettings? settings)
    {
        if (settings?.Tiers == null) return [("serverBooster", "xp.settings.serverBoosterRequired")];

        var errors = new List<(string Field, string ErrorKey)>();
        if (settings.Tiers.Count > MaximumTiers)
            errors.Add(("serverBooster.tiers", "xp.settings.serverBoosterTierCount"));

        for (var index = 0; index < settings.Tiers.Count; index++)
        {
            var tier = settings.Tiers[index];
            if (tier == null || tier.MinimumBoostMonths < 0)
                errors.Add(($"serverBooster.tiers[{index}].minimumBoostMonths", "xp.settings.serverBoosterMonths"));
            if (tier == null || tier.Multiplier < 1m || tier.Multiplier > 10m || DecimalPlaces(tier.Multiplier) > 2)
                errors.Add(($"serverBooster.tiers[{index}].multiplier", "xp.settings.serverBoosterMultiplier"));
        }

        if (settings.Tiers.Where(x => x != null).GroupBy(x => x.MinimumBoostMonths).Any(group => group.Count() > 1))
            errors.Add(("serverBooster.tiers", "xp.settings.serverBoosterDuplicateMonths"));

        var sorted = settings.Tiers.Where(x => x != null).OrderBy(x => x.MinimumBoostMonths).ToArray();
        if (sorted.Zip(sorted.Skip(1), (earlier, later) => later.Multiplier < earlier.Multiplier).Any(decreases => decreases))
            errors.Add(("serverBooster.tiers", "xp.settings.serverBoosterOrder"));

        return errors;
    }

    public static void Normalize(ServerBoosterXpSettings settings) =>
        settings.Tiers = settings.Tiers.OrderBy(x => x.MinimumBoostMonths).ToList();

    private static int DecimalPlaces(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0x7F;
}

public sealed record AutomaticXpMultiplierResult(decimal Amount, decimal AppliedServerBoosterMultiplier);

public sealed class ServerBoosterXpMultiplierResolver(TimeProvider timeProvider)
{
    private static readonly HashSet<string> SupportedSources =
    [
        "message", "reaction", "thread_create", "thread_message", "event_interest", "voice"
    ];

    public decimal Resolve(GuildXpSettings settings, IGuildUser? guildUser) =>
        Resolve(settings, guildUser?.PremiumSince, timeProvider.GetUtcNow());

    public decimal Resolve(GuildXpSettings settings, DateTimeOffset? premiumSince, DateTimeOffset now)
    {
        if (!settings.ServerBooster.Enabled || premiumSince == null || premiumSince > now) return 1m;

        return settings.ServerBooster.Tiers
            .Where(tier => premiumSince.Value.AddMonths(tier.MinimumBoostMonths) <= now)
            .OrderByDescending(tier => tier.MinimumBoostMonths)
            .Select(tier => tier.Multiplier)
            .FirstOrDefault(1m);
    }

    public AutomaticXpMultiplierResult Apply(string source, decimal baseAmount, decimal existingMultiplier, GuildXpSettings settings, IGuildUser? guildUser)
        => Apply(source, baseAmount, existingMultiplier, settings, guildUser?.PremiumSince, timeProvider.GetUtcNow());

    public AutomaticXpMultiplierResult Apply(string source, decimal baseAmount, decimal existingMultiplier, GuildXpSettings settings, DateTimeOffset? premiumSince, DateTimeOffset now)
    {
        if (baseAmount <= 0 || !SupportedSources.Contains(source)) return new(baseAmount * existingMultiplier, 1m);
        var boosterMultiplier = Resolve(settings, premiumSince, now);
        return new(baseAmount * existingMultiplier * boosterMultiplier, boosterMultiplier);
    }
}
