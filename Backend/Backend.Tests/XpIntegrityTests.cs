using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class XpIntegrityTests
{
    [Fact]
    public void Automation_requires_explicit_enablement_and_has_bounded_defaults()
    {
        var options = new XpIntegrityOptions();
        Assert.False(options.Enabled);
        Assert.False(options.CatchUpAnnouncements);
        Assert.Equal(60, options.IntervalMinutes);
        Assert.Equal(20, options.MaxAnnouncementsPerGuildPerRun);
        Assert.True(XpIntegrityOptions.IsValid(options));
    }

    [Theory]
    [InlineData(4, 100, 90, 20)]
    [InlineData(1441, 100, 90, 20)]
    [InlineData(60, 0, 90, 20)]
    [InlineData(60, 501, 90, 20)]
    [InlineData(60, 100, 9, 20)]
    [InlineData(60, 100, 301, 20)]
    [InlineData(60, 100, 90, 0)]
    [InlineData(60, 100, 90, 101)]
    public void Invalid_sweep_limits_are_rejected(int minutes, int batch, int timeout, int announcements) =>
        Assert.False(XpIntegrityOptions.IsValid(new() { IntervalMinutes = minutes, BatchSize = batch, MemberTimeoutSeconds = timeout, MaxAnnouncementsPerGuildPerRun = announcements }));

    [Fact]
    public void Catch_up_keys_are_stable_and_isolated_by_member_scope_season_and_level()
    {
        var key = XpIntegrityService.CatchUpKey(1, 2, LevelProgressScope.Lifetime, null, 10);
        Assert.Equal(key, XpIntegrityService.CatchUpKey(1, 2, LevelProgressScope.Lifetime, null, 10));
        Assert.Equal(5, new HashSet<string>
        {
            key, XpIntegrityService.CatchUpKey(1, 3, LevelProgressScope.Lifetime, null, 10),
            XpIntegrityService.CatchUpKey(2, 2, LevelProgressScope.Lifetime, null, 10),
            XpIntegrityService.CatchUpKey(1, 2, LevelProgressScope.Season, "season", 10),
            XpIntegrityService.CatchUpKey(1, 2, LevelProgressScope.Lifetime, null, 11)
        }.Count);
    }

    [Theory]
    [InlineData(100, 0, 100, 0, false)]
    [InlineData(0, 0, 100, 0, false)]
    [InlineData(100, 0, 0, 0, true)]
    [InlineData(100, 10, 100, 0, true)]
    [InlineData(100, -10, 100, -10, false)]
    public void Missing_historical_evidence_never_silently_deletes_existing_xp(decimal earned, decimal manual, decimal expectedEarned, decimal expectedManual, bool review) =>
        Assert.Equal(review, XpIntegrityService.WouldReduceSources(earned, manual, new(expectedEarned, expectedManual, 0)));
}
