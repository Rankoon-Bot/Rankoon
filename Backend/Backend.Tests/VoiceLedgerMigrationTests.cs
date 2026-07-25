using MongoDB.Bson;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class VoiceLedgerMigrationTests
{
    [Fact]
    public void Legacy_entry_is_split_at_UTC_midnight_without_losing_seconds_or_xp()
    {
        var start = new DateTime(2026, 1, 1, 23, 59, 30, DateTimeKind.Utc);
        var entry = EligibleEntry(start, start.AddMinutes(2), 12m);

        Assert.True(VoiceLedgerMigrationService.TryPlan(entry, out var parts));

        Assert.Equal(2, parts.Count);
        Assert.Equal(new[] { 30L, 90L }, parts.Select(x => x.TotalEligibleSeconds));
        Assert.Equal(12m, parts.Sum(x => x.TotalAwardedXp));
        Assert.Equal(parts[0].SessionCursors[0].SessionId, parts[1].SessionCursors[0].SessionId);
        Assert.All(parts, part => Assert.All(part.Segments, segment => Assert.Equal(VoiceLedgerMigrationService.LegacySettingsRevision, segment.SettingsRevision)));
        Assert.Equal(VoiceActivityProjectionStatus.Applied, parts[0].ProjectionStatus);
        Assert.Equal(parts[0].TotalAwardedXp, parts[0].ProjectedXp);
        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), parts[0].Segments[0].EndsAtUtc);
    }

    [Fact]
    public void Effective_legacy_automatic_kind_is_accepted_but_reversals_and_pending_entries_are_not()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var automatic = EligibleEntry(start, start.AddMinutes(1), 5m);
        automatic.Kind = null;
        var reversal = EligibleEntry(start, start.AddMinutes(1), -5m);
        reversal.Kind = XpLedgerEntryKind.AutomaticReversal;
        var pending = EligibleEntry(start, start.AddMinutes(1), 5m);
        pending.ProjectionStatus = SeasonProjectionStatus.Pending;

        Assert.True(VoiceLedgerMigrationService.IsEligible(automatic));
        Assert.False(VoiceLedgerMigrationService.IsEligible(reversal));
        Assert.False(VoiceLedgerMigrationService.IsEligible(pending));
    }

    [Fact]
    public void Legacy_session_id_is_compact_deterministic_and_collision_free_for_object_ids()
    {
        const string firstId = "64b000000000000000000001";
        const string secondId = "64b000000000000000000002";

        var first = VoiceLedgerMigrationService.LegacySessionId(firstId);

        Assert.Equal(first, VoiceLedgerMigrationService.LegacySessionId(firstId));
        Assert.NotEqual(first, VoiceLedgerMigrationService.LegacySessionId(secondId));
        Assert.StartsWith("lv_", first);
        Assert.Equal(19, first.Length);
    }

    [Fact]
    public void Invalid_voice_shapes_are_not_migrated()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var noChannel = EligibleEntry(start, start.AddMinutes(1), 5m);
        noChannel.ChannelId = null;
        var noDuration = EligibleEntry(start, start, 5m);
        var projectionControl = EligibleEntry(start, start.AddMinutes(1), 5m);
        projectionControl.IsProjectionControl = true;

        Assert.False(VoiceLedgerMigrationService.IsEligible(noChannel));
        Assert.False(VoiceLedgerMigrationService.IsEligible(noDuration));
        Assert.False(VoiceLedgerMigrationService.IsEligible(projectionControl));
    }

    [Theory]
    [InlineData(VoiceLedgerMigrationPhase.Copying, true, false)]
    [InlineData(VoiceLedgerMigrationPhase.Verifying, true, false)]
    [InlineData(VoiceLedgerMigrationPhase.ParityFailed, false, false)]
    [InlineData(VoiceLedgerMigrationPhase.AwaitingDeletionApproval, true, true)]
    [InlineData(VoiceLedgerMigrationPhase.Deleting, true, true)]
    [InlineData(VoiceLedgerMigrationPhase.Completed, true, true)]
    public void Compressed_authority_requires_cutover_phase_and_matching_parity(VoiceLedgerMigrationPhase phase, bool matches, bool expected)
    {
        var state = new VoiceLedgerMigrationState
        {
            Phase = phase,
            Parity = new VoiceLedgerMigrationParityReport { Matches = matches, GroupedParityVerified = true, Fingerprint = "report" },
            DeletionApprovalFingerprint = "report"
        };

        Assert.Equal(expected, VoiceLedgerMigrationService.IsCompressedVoiceAuthoritative(state));
    }

    [Fact]
    public void Deleting_requires_approval_for_the_current_report()
    {
        var state = new VoiceLedgerMigrationState
        {
            Phase = VoiceLedgerMigrationPhase.Deleting,
            Parity = new VoiceLedgerMigrationParityReport { Matches = true, GroupedParityVerified = true, Fingerprint = "current" },
            DeletionApprovalFingerprint = "old"
        };

        Assert.False(VoiceLedgerMigrationService.IsCompressedVoiceAuthoritative(state));
    }

    [Theory]
    [InlineData(VoiceLedgerMigrationPhase.Copying, true, false)]
    [InlineData(VoiceLedgerMigrationPhase.ParityFailed, true, false)]
    [InlineData(VoiceLedgerMigrationPhase.AwaitingDeletionApproval, false, true)]
    public void Audit_authority_keeps_exactly_one_migrated_history_source(VoiceLedgerMigrationPhase phase, bool legacyLedger, bool migratedCompressed)
    {
        var state = new VoiceLedgerMigrationState
        {
            Phase = phase,
            Parity = new VoiceLedgerMigrationParityReport { Matches = phase != VoiceLedgerMigrationPhase.ParityFailed, GroupedParityVerified = true, Fingerprint = "report" },
            DeletionApprovalFingerprint = "report"
        };

        var authority = XpAuditService.HistoryAuthority(state);

        Assert.Equal(legacyLedger, authority.IncludeLegacyLedger);
        Assert.Equal(migratedCompressed, authority.IncludeMigratedCompressedSegments);
    }

    [Fact]
    public void Grouped_parity_rejects_equal_global_totals_assigned_to_different_members_or_seasons()
    {
        var sourceMembers = VoiceLedgerMigrationService.MemberTotals([(1UL, 10UL, 60L, 10m), (1UL, 20UL, 30L, 5m)]);
        var targetMembers = VoiceLedgerMigrationService.MemberTotals([(1UL, 10UL, 30L, 5m), (1UL, 20UL, 60L, 10m)]);
        var sourceSeasons = VoiceLedgerMigrationService.MemberSeasonTotals([(1UL, 10UL, "64b000000000000000000001", 60L, 10m)]);
        var targetSeasons = VoiceLedgerMigrationService.MemberSeasonTotals([(1UL, 10UL, "64b000000000000000000002", 60L, 10m)]);

        Assert.Equal(sourceMembers.Sum(x => x.AwardedXp), targetMembers.Sum(x => x.AwardedXp));
        Assert.False(VoiceLedgerMigrationService.MemberParity(sourceMembers, targetMembers));
        Assert.False(VoiceLedgerMigrationService.MemberSeasonParity(sourceSeasons, targetSeasons));
    }

    private static XpLedgerEntry EligibleEntry(DateTime start, DateTime end, decimal amount) => new()
    {
        Id = ObjectId.GenerateNewId().ToString(),
        GrantKey = Guid.NewGuid().ToString("N"),
        GuildId = 1,
        UserId = 2,
        DisplayName = "member",
        Source = "voice",
        Amount = amount,
        ChannelId = 3,
        PeriodStartsAtUtc = start,
        PeriodEndsAtUtc = end,
        OccurredAtUtc = start,
        ProjectionStatus = SeasonProjectionStatus.Applied,
        ProjectedAtUtc = end
    };
}
