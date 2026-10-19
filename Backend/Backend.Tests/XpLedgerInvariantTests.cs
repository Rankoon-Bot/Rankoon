using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class XpLedgerInvariantTests
{
    private static XpGrantRequest Grant => new(1, 2, "Member", "message", 5, "message:1", DateTime.UnixEpoch);

    [Theory]
    [InlineData("guild")]
    [InlineData("member")]
    [InlineData("key")]
    [InlineData("source")]
    [InlineData("negative")]
    [InlineData("negativeCooldown")]
    [InlineData("unsupportedCooldown")]
    [InlineData("reversal")]
    [InlineData("missingEnd")]
    [InlineData("missingStart")]
    [InlineData("backwardsPeriod")]
    public void Invalid_grants_are_rejected_before_any_database_write(string invalid)
    {
        var request = invalid switch
        {
            "guild" => Grant with { GuildId = 0 },
            "member" => Grant with { UserId = 0 },
            "key" => Grant with { GrantKey = " " },
            "source" => Grant with { Source = " " },
            "negative" => Grant with { Amount = -1 },
            "negativeCooldown" => Grant with { CooldownSeconds = -1 },
            "unsupportedCooldown" => Grant with { Source = "voice", CooldownSeconds = 60 },
            "reversal" => Grant with { ReversesGrantKey = "message:0" },
            "missingEnd" => Grant with { PeriodStartsAtUtc = DateTime.UnixEpoch },
            "missingStart" => Grant with { PeriodEndsAtUtc = DateTime.UnixEpoch },
            _ => Grant with { PeriodStartsAtUtc = DateTime.UnixEpoch, PeriodEndsAtUtc = DateTime.UnixEpoch.AddSeconds(-1) }
        };
        Assert.ThrowsAny<ArgumentException>(() => XpService.ValidateGrant(request));
    }

    [Theory]
    [InlineData("message", 60)]
    [InlineData("reaction", 30)]
    [InlineData("thread_message", 60)]
    [InlineData("thread_create", 0)]
    [InlineData("event_interest", 0)]
    [InlineData("voice", 0)]
    public void Valid_source_cooldowns_are_accepted(string source, int seconds) =>
        XpService.ValidateGrant(Grant with { Source = source, CooldownSeconds = seconds });

    [Theory]
    [InlineData(XpLedgerScope.LifetimeOnly, true, false)]
    [InlineData(XpLedgerScope.LifetimeAndSeason, true, true)]
    [InlineData(XpLedgerScope.SeasonOnly, false, true)]
    public void Reversal_preserves_original_identity_amount_season_and_scope(XpLedgerScope scope, bool lifetime, bool season)
    {
        var original = new XpLedgerEntry { Id = "64b000000000000000000001", GuildId = 1, UserId = 2, DisplayName = "Name", GrantKey = "original", Source = "reaction", Amount = 10.25m, ChannelId = 3, SeasonId = "64b000000000000000000002", Scope = scope };
        var reversal = XpService.CreateAutomaticReversal(original, "reversal", DateTime.UnixEpoch);
        Assert.Equal(-10.25m, reversal.Amount);
        Assert.Equal(original.GuildId, reversal.GuildId);
        Assert.Equal(original.UserId, reversal.UserId);
        Assert.Equal(original.SeasonId, reversal.SeasonId);
        Assert.Equal(original.Id, reversal.ReversesLedgerEntryId);
        Assert.Equal(original.GrantKey, reversal.ReversesGrantKey);
        Assert.Equal(XpLedgerEntryKind.AutomaticReversal, reversal.Kind);
        Assert.Equal(lifetime, XpLedgerSemantics.AffectsLifetime(reversal));
        Assert.Equal(season, XpLedgerSemantics.AffectsSeason(reversal));
    }

    [Fact]
    public void Every_level_boundary_inverts_the_curve_and_preserves_fractional_xp()
    {
        long previous = 0;
        for (var level = 1; level <= 10000; level++)
        {
            var threshold = Mee6LevelCurve.RequiredXpForLevel(level);
            Assert.True(threshold > previous);
            Assert.Equal(level - 1, Mee6LevelCurve.GetLevel(threshold - 0.000001m));
            Assert.Equal(level, Mee6LevelCurve.GetLevel(threshold));
            previous = threshold;
        }
        Assert.Equal(0, Mee6LevelCurve.GetLevel(-100));
    }

    [Fact]
    public void Transition_snapshot_preserves_multi_level_jumps_and_demotions()
    {
        var gain = XpService.TransitionSnapshot(99, 10000);
        Assert.Equal(0, gain.PreviousLevel);
        Assert.True(gain.NewLevel > 1);
        var reversal = XpService.TransitionSnapshot(10000, 99);
        Assert.Equal(gain.NewLevel, reversal.PreviousLevel);
        Assert.Equal(0, reversal.NewLevel);
    }
}
