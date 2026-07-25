using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class XpProjectionTests
{
    [Fact]
    public void Xp_ledger_projection_state_is_bounded_to_the_ledger_entry()
    {
        var ledger = new XpLedgerEntry { ProjectionLeaseOwner = "worker", ProjectionLeaseExpiresAtUtc = DateTime.UnixEpoch };
        var member = new MemberXp();
        var stats = new GuildStats();

        var ledgerDocument = ledger.ToBsonDocument();
        Assert.Equal("worker", ledgerDocument["projection_lease_owner"].AsString);
        Assert.False(member.ToBsonDocument().Contains("applied_ledger_keys"));
        Assert.False(stats.ToBsonDocument().Contains("applied_ledger_keys"));
    }

    [Fact]
    public void Voice_seconds_only_count_valid_voice_ledger_intervals()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var voice = new XpLedgerEntry { Source = "voice", PeriodStartsAtUtc = start, PeriodEndsAtUtc = start.AddSeconds(90) };
        var message = new XpLedgerEntry { Source = "message", PeriodStartsAtUtc = start, PeriodEndsAtUtc = start.AddSeconds(90) };

        Assert.Equal(90L, (long)Invoke("VoiceSeconds", voice)!);
        Assert.Equal(0L, (long)Invoke("VoiceSeconds", message)!);
    }

    [Fact]
    public void Cooldown_fields_are_persisted_on_ledger_and_member_documents()
    {
        var ledger = new XpLedgerEntry { CooldownSource = "reaction", CooldownSeconds = 30, CooldownAcquired = true, IsProjectionControl = true };
        var member = new MemberXp { LastReactionXpAt = DateTime.UnixEpoch, LastReactionXpGrantKey = "reaction:1:2:a" };

        var ledgerDocument = ledger.ToBsonDocument();
        var memberDocument = member.ToBsonDocument();

        Assert.Equal("reaction", ledgerDocument["cooldown_source"].AsString);
        Assert.Equal(30, ledgerDocument["cooldown_seconds"].ToInt32());
        Assert.True(ledgerDocument["cooldown_acquired"].AsBoolean);
        Assert.True(ledgerDocument["is_projection_control"].AsBoolean);
        Assert.Equal("reaction:1:2:a", memberDocument["last_reaction_xp_grant_key"].AsString);
    }

    [Fact]
    public void Only_automatic_event_interest_grants_can_be_reversed_as_event_interest()
    {
        var automaticGrant = new XpLedgerEntry { Source = "event_interest", Kind = XpLedgerEntryKind.AutomaticGrant };
        var manualAdjustment = new XpLedgerEntry { Source = "event_interest", Kind = XpLedgerEntryKind.ManualAdjustment };
        var otherAutomaticGrant = new XpLedgerEntry { Source = "reaction", Kind = XpLedgerEntryKind.AutomaticGrant };

        Assert.True(XpService.MatchesAutomaticGrant(automaticGrant, "event_interest"));
        Assert.False(XpService.MatchesAutomaticGrant(manualAdjustment, "event_interest"));
        Assert.False(XpService.MatchesAutomaticGrant(otherAutomaticGrant, "event_interest"));
    }

    [Fact]
    public void Recovery_snapshot_uses_claimed_revision_but_only_projected_totals_from_newer_pending_days()
    {
        var claimed = new VoiceActivityDay { Id = "a", TotalEligibleSeconds = 10, TotalAwardedXp = 2m, SeasonTotals = [new VoiceSeasonTotal { SeasonId = "s", EligibleSeconds = 10, AwardedXp = 2m }] };
        var pending = new VoiceActivityDay { Id = "b", TotalEligibleSeconds = 20, TotalAwardedXp = 4m, ProjectedEligibleSeconds = 5, ProjectedXp = 1m, ProjectedSeasonTotals = [new VoiceSeasonTotal { SeasonId = "s", EligibleSeconds = 5, AwardedXp = 1m }] };

        var snapshot = VoiceActivityProjectionService.Snapshot([claimed, pending], claimed);

        Assert.Equal(15, snapshot.EligibleSeconds);
        Assert.Equal(3m, snapshot.AwardedXp);
        Assert.Equal(15, snapshot.Seasons["s"].EligibleSeconds);
    }

    [Fact]
    public void Expired_lease_recovery_uses_persisted_target_not_new_activity()
    {
        var day = new VoiceActivityDay
        {
            Id = "a", TotalEligibleSeconds = 20, TotalAwardedXp = 4m, ProjectionRevision = 2,
            ProjectionTargetRevision = 1, ProjectionTargetEligibleSeconds = 10, ProjectionTargetAwardedXp = 2m,
            ProjectionTargetSeasonTotals = [new VoiceSeasonTotal { SeasonId = "s", EligibleSeconds = 10, AwardedXp = 2m }]
        };

        var target = VoiceActivityProjectionService.ProjectionTarget(day);

        Assert.Equal(1, target.ProjectionRevision);
        Assert.Equal(10, target.TotalEligibleSeconds);
        Assert.Equal(2m, target.TotalAwardedXp);
    }

    [Theory]
    [InlineData(SeasonStatus.Active, true)]
    [InlineData(SeasonStatus.Closing, true)]
    [InlineData(SeasonStatus.Closed, false)]
    [InlineData(null, false)]
    public void Live_voice_projection_never_mutates_closed_or_missing_seasons(SeasonStatus? status, bool expected)
    {
        Assert.Equal(expected, VoiceActivityProjectionService.CanMutateSeason(status));
    }

    [Fact]
    public void Pre_cutover_snapshot_excludes_only_migrated_legacy_segments()
    {
        var day = new VoiceActivityDay
        {
            Id = "a", ProjectedEligibleSeconds = 15, ProjectedXp = 3m,
            ProjectedSeasonTotals = [new VoiceSeasonTotal { SeasonId = "s", EligibleSeconds = 15, AwardedXp = 3m }],
            Segments =
            [
                new VoiceActivitySegment { SettingsRevision = VoiceLedgerMigrationService.LegacySettingsRevision, EligibleSeconds = 10, AwardedXp = 2m, SeasonId = "s" },
                new VoiceActivitySegment { SettingsRevision = 1, EligibleSeconds = 5, AwardedXp = 1m, SeasonId = "s" }
            ]
        };

        var snapshot = VoiceActivityProjectionService.Snapshot([day], new VoiceActivityDay(), includeLegacy: false);

        Assert.Equal(5, snapshot.EligibleSeconds);
        Assert.Equal(1m, snapshot.AwardedXp);
        Assert.Equal(1m, snapshot.Seasons["s"].AwardedXp);
    }

    private static object? Invoke(string name, params object[] arguments) => typeof(XpService)
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, arguments);
}
