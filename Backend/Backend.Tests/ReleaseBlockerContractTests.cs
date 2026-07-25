using MongoDB.Bson;
using Rankoon.Data.Dashboard;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.Reporting;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class ReleaseBlockerContractTests
{
    [Fact]
    public void Voice_transition_cause_is_self_contained_and_deterministic()
    {
        var transition = new LevelTransitionEvent
        {
            EventKey = LevelTransitionService.CreateEventKey("voice:day:7"), CauseKey = "voice:day:7", Source = "voice",
            SourceChannelId = 42, GainedXp = 12.5m, PreviousTotalXp = 90, NewTotalXp = 102
        };

        var cause = LevelProgressionWorker.ResolveCause(transition, null);
        var document = transition.ToBsonDocument();

        Assert.Equal("level-transition:voice:day:7:lifetime", transition.EventKey);
        Assert.Equal("voice", cause.Source);
        Assert.Equal(42UL, cause.ChannelId);
        Assert.Equal(12.5m, cause.GainedXp);
        Assert.False(cause.SuppressAnnouncement);
        Assert.False(document.Contains("ledger_grant_key"));
    }

    [Fact]
    public void Legacy_ledger_transition_keeps_ledger_context_and_migration_suppression()
    {
        var transition = new LevelTransitionEvent { EventKey = "event", LedgerGrantKey = "grant", Source = "system" };
        var ledger = new XpLedgerEntry { GrantKey = "grant", Kind = XpLedgerEntryKind.SystemMigration, ChannelId = 7 };

        var cause = LevelProgressionWorker.ResolveCause(transition, ledger);

        Assert.Equal("grant", cause.Key);
        Assert.Equal(7UL, cause.ChannelId);
        Assert.True(cause.SuppressAnnouncement);
    }

    [Fact]
    public void Voice_history_selector_switches_authority_without_double_counting()
    {
        var at = new DateTime(2026, 7, 24, 10, 0, 0, DateTimeKind.Utc);
        var day = new VoiceActivityDay
        {
            UserId = 9,
            Segments =
            [
                new VoiceActivitySegment { StartsAtUtc = at, AwardedXp = 5, EligibleSeconds = 60, ChannelId = 1, SettingsRevision = VoiceLedgerMigrationService.LegacySettingsRevision },
                new VoiceActivitySegment { StartsAtUtc = at.AddMinutes(1), AwardedXp = 3, EligibleSeconds = 30, ChannelId = 2, SettingsRevision = 2 }
            ]
        };

        var beforeCutover = VoiceActivityReadModel.Select([day], false, at.Date, at.Date.AddDays(1));
        var afterCutover = VoiceActivityReadModel.Select([day], true, at.Date, at.Date.AddDays(1));

        Assert.Equal(3m, Assert.Single(beforeCutover).AwardedXp);
        Assert.Equal(8m, afterCutover.Sum(x => x.AwardedXp));
        Assert.Equal(90, afterCutover.Sum(x => x.EligibleSeconds));
        Assert.Equal([9UL], afterCutover.Select(x => x.UserId).Distinct());
    }

    [Fact]
    public void Dashboard_activity_accepts_voice_day_rows_after_ledger_deletion()
    {
        var start = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc);
        var activity = DashboardOverviewService.BuildActivity(
            [new DashboardOverviewService.ActivityRow(4, start.AddHours(1), "voice", 6, 120)], start, 1, Array.Empty<ReportEvent>());

        Assert.Equal(6m, activity.XpAwarded);
        Assert.Equal(120, activity.VoiceSeconds);
        Assert.Equal(1, activity.ActiveMemberCount);
    }

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(3, 2, false)]
    [InlineData(0, 0, false)]
    public void Coordinator_ownership_requires_every_key(int expected, long owned, bool result) =>
        Assert.Equal(result, XpProjectionCoordinator.OwnsAll(expected, owned));

    [Theory]
    [InlineData("voice", true)]
    [InlineData("VOICE", true)]
    [InlineData("message", false)]
    [InlineData(null, false)]
    public void Raw_entries_reject_voice_source(string? source, bool requiresTimeline) =>
        Assert.Equal(requiresTimeline, XpAuditService.RequiresVoiceTimeline(source));
}
