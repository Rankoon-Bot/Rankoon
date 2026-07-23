using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class ServerBoosterXpTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly ServerBoosterXpMultiplierResolver _resolver = new(TimeProvider.System);

    [Fact]
    public void Disabled_module_is_neutral_and_keeps_tiers()
    {
        var settings = Settings(false, (0, 1.5m));

        Assert.Equal(1m, _resolver.Resolve(settings, Now.AddYears(-1), Now));
        Assert.Single(settings.ServerBooster.Tiers);
    }

    [Fact]
    public void Missing_member_or_premium_since_is_neutral()
    {
        var settings = Settings(true, (0, 1.5m));

        Assert.Equal(1m, _resolver.Resolve(settings, (DateTimeOffset?)null, Now));
        Assert.Equal(1m, _resolver.Resolve(settings, guildUser: null));
    }

    [Fact]
    public void Booster_before_first_threshold_is_neutral()
    {
        Assert.Equal(1m, _resolver.Resolve(Settings(true, (2, 1.5m)), Now.AddMonths(-1), Now));
    }

    [Fact]
    public void Highest_reached_tier_applies_between_and_above_thresholds()
    {
        var settings = Settings(true, (0, 1.25m), (2, 1.5m), (4, 1.75m));

        Assert.Equal(1.5m, _resolver.Resolve(settings, Now.AddMonths(-3), Now));
        Assert.Equal(1.75m, _resolver.Resolve(settings, Now.AddYears(-2), Now));
    }

    [Fact]
    public void Exact_threshold_instant_uses_new_tier()
    {
        var premiumSince = new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero);
        var threshold = premiumSince.AddMonths(2);

        Assert.Equal(1.25m, _resolver.Resolve(Settings(true, (0, 1.25m), (2, 1.5m)), premiumSince, threshold.AddTicks(-1)));
        Assert.Equal(1.5m, _resolver.Resolve(Settings(true, (0, 1.25m), (2, 1.5m)), premiumSince, threshold));
    }

    [Fact]
    public void Unsorted_tiers_are_evaluated_and_normalized_by_month()
    {
        var settings = Settings(true, (4, 1.75m), (0, 1.25m), (2, 1.5m));

        Assert.Equal(1.5m, _resolver.Resolve(settings, Now.AddMonths(-3), Now));
        ServerBoosterXpSettingsValidator.Normalize(settings.ServerBooster);
        Assert.Equal(new[] { 0, 2, 4 }, settings.ServerBooster.Tiers.Select(x => x.MinimumBoostMonths));
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void Invalid_tiers_are_rejected(ServerBoosterXpSettings settings, string errorKey)
    {
        Assert.Contains(ServerBoosterXpSettingsValidator.Validate(settings), error => error.ErrorKey == errorKey);
    }

    public static TheoryData<ServerBoosterXpSettings, string> InvalidConfigurations => new()
    {
        { Booster((0, 1.25m), (0, 1.5m)), "xp.settings.serverBoosterDuplicateMonths" },
        { Booster((-1, 1.25m)), "xp.settings.serverBoosterMonths" },
        { Booster((0, 0.99m)), "xp.settings.serverBoosterMultiplier" },
        { Booster((0, 1.001m)), "xp.settings.serverBoosterMultiplier" },
        { Booster((0, 10.01m)), "xp.settings.serverBoosterMultiplier" },
        { Booster((0, 1.75m), (4, 1.25m)), "xp.settings.serverBoosterOrder" },
        { new ServerBoosterXpSettings { Tiers = Enumerable.Range(0, 11).Select(x => new ServerBoosterXpTier { MinimumBoostMonths = x, Multiplier = 1m }).ToList() }, "xp.settings.serverBoosterTierCount" }
    };

    [Fact]
    public void Neutral_multiplier_is_valid()
    {
        Assert.Empty(ServerBoosterXpSettingsValidator.Validate(Booster((0, 1m))));
    }

    [Fact]
    public void Existing_channel_and_booster_multipliers_are_each_applied_once()
    {
        var result = _resolver.Apply("voice", 10m, 1.2m, Settings(true, (0, 1.5m)), Now.AddMonths(-1), Now);

        Assert.Equal(18m, result.Amount);
        Assert.Equal(1.5m, result.AppliedServerBoosterMultiplier);
    }

    [Theory]
    [InlineData("mee6_import")]
    [InlineData("manual_adjustment")]
    public void Non_activity_sources_do_not_receive_booster_bonus(string source)
    {
        var result = _resolver.Apply(source, 10m, 1m, Settings(true, (0, 2m)), Now.AddYears(-1), Now);

        Assert.Equal(10m, result.Amount);
        Assert.Equal(1m, result.AppliedServerBoosterMultiplier);
    }

    [Fact]
    public void Negative_grants_do_not_receive_booster_bonus()
    {
        var result = _resolver.Apply("reaction", -10m, 1m, Settings(true, (0, 2m)), Now.AddYears(-1), Now);

        Assert.Equal(-10m, result.Amount);
        Assert.Equal(1m, result.AppliedServerBoosterMultiplier);
    }

    [Fact]
    public void Existing_ledger_amount_is_not_changed_by_later_configuration_changes()
    {
        var ledger = new XpLedgerEntry { Amount = 15m, AppliedServerBoosterMultiplier = 1.5m };
        var settings = Settings(true, (0, 2m));

        _ = _resolver.Resolve(settings, Now.AddYears(-1), Now);

        Assert.Equal(15m, ledger.Amount);
        Assert.Equal(1.5m, ledger.AppliedServerBoosterMultiplier);
    }

    [Fact]
    public void Reversal_uses_original_awarded_amount_without_a_new_booster_factor()
    {
        var original = new XpLedgerEntry { Id = "507f1f77bcf86cd799439011", GrantKey = "reaction:1", Source = "reaction", Amount = 15m, AppliedServerBoosterMultiplier = 1.5m };

        var reversal = XpService.CreateAutomaticReversal(original, "reaction-remove:1", Now.UtcDateTime);

        Assert.Equal(-15m, reversal.Amount);
        Assert.Null(reversal.AppliedServerBoosterMultiplier);
        Assert.Equal(original.Id, reversal.ReversesLedgerEntryId);
    }

    [Fact]
    public void Old_settings_document_without_booster_fields_uses_compatible_defaults()
    {
        var document = new BsonDocument { ["guild_id"] = 1L, ["enabled"] = true };

        var settings = BsonSerializer.Deserialize<GuildXpSettings>(document);

        Assert.False(settings.ServerBooster.Enabled);
        Assert.Empty(settings.ServerBooster.Tiers);
    }

    [Theory]
    [InlineData(2026, 1, 31, 1, 2026, 2, 28)]
    [InlineData(2025, 12, 31, 1, 2026, 1, 31)]
    [InlineData(2024, 2, 29, 12, 2025, 2, 28)]
    public void Calendar_month_boundaries_follow_add_months(int startYear, int startMonth, int startDay, int months, int endYear, int endMonth, int endDay)
    {
        var start = new DateTimeOffset(startYear, startMonth, startDay, 12, 0, 0, TimeSpan.Zero);
        var threshold = new DateTimeOffset(endYear, endMonth, endDay, 12, 0, 0, TimeSpan.Zero);
        var settings = Settings(true, (months, 1.5m));

        Assert.Equal(1m, _resolver.Resolve(settings, start, threshold.AddTicks(-1)));
        Assert.Equal(1.5m, _resolver.Resolve(settings, start, threshold));
    }

    [Fact]
    public void Applied_booster_multiplier_is_optional_audit_data()
    {
        var boosted = new XpLedgerEntry { AppliedServerBoosterMultiplier = 1.5m }.ToBsonDocument();
        var neutral = new XpLedgerEntry().ToBsonDocument();

        Assert.Equal(1.5m, boosted["applied_server_booster_multiplier"].ToDecimal());
        Assert.False(neutral.Contains("applied_server_booster_multiplier"));
    }

    private static GuildXpSettings Settings(bool enabled, params (int Months, decimal Multiplier)[] tiers)
    {
        var booster = Booster(tiers);
        booster.Enabled = enabled;
        return new GuildXpSettings { ServerBooster = booster };
    }

    private static ServerBoosterXpSettings Booster(params (int Months, decimal Multiplier)[] tiers) => new()
    {
        Enabled = true,
        Tiers = tiers.Select(tier => new ServerBoosterXpTier { MinimumBoostMonths = tier.Months, Multiplier = tier.Multiplier }).ToList()
    };
}
