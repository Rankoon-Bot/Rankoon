using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class VoiceActivityAccumulatorTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Eight_hours_of_five_second_slices_compress_to_one_day_segment_and_cursor()
    {
        VoiceActivityDay? day = null;
        for (var index = 0; index < 5760; index++)
        {
            var from = Start.AddSeconds(index * 5);
            day = VoiceActivityAccumulator.Plan(day, Slice(from, from.AddSeconds(5), xp: 0.833333m)).Replacement;
        }

        Assert.NotNull(day);
        Assert.Single(day.Segments);
        Assert.Single(day.SessionCursors);
        Assert.Equal(Start.AddHours(8), day.SessionCursors[0].ProcessedThroughUtc);
        Assert.Equal(28_800, day.TotalEligibleSeconds);
        Assert.Equal(5760 * 0.833333m, day.TotalAwardedXp);
    }

    [Fact]
    public void Full_overlap_is_duplicate_and_partial_overlap_is_trimmed()
    {
        var first = VoiceActivityAccumulator.Plan(null, Slice(Start, Start.AddSeconds(10), 10m)).Replacement!;
        var duplicate = VoiceActivityAccumulator.Plan(first, Slice(Start.AddSeconds(2), Start.AddSeconds(8), 6m));
        var partial = VoiceActivityAccumulator.Plan(first, Slice(Start.AddSeconds(5), Start.AddSeconds(15), 10m));

        Assert.True(duplicate.Duplicate);
        Assert.True(partial.PartialOverlap);
        Assert.Equal(Start.AddSeconds(10), partial.Segment!.StartsAtUtc);
        Assert.Equal(5, partial.Segment.EligibleSeconds);
        Assert.Equal(0.833333m, partial.Segment.AwardedXp);
        Assert.Equal(15, partial.Replacement!.TotalEligibleSeconds);
    }

    [Fact]
    public void Cursor_gap_is_reported_without_inventing_activity()
    {
        var first = VoiceActivityAccumulator.Plan(null, Slice(Start, Start.AddSeconds(5), 1m)).Replacement!;
        var plan = VoiceActivityAccumulator.Plan(first, Slice(Start.AddSeconds(10), Start.AddSeconds(15), 1m));

        Assert.True(plan.GapDetected);
        Assert.Equal(10, plan.Replacement!.TotalEligibleSeconds);
        Assert.Equal(2, plan.Replacement.Segments.Count);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("season")]
    [InlineData("rate")]
    [InlineData("channelMultiplier")]
    [InlineData("booster")]
    [InlineData("settings")]
    public void Every_merge_signature_field_prevents_incompatible_compaction(string changed)
    {
        var left = Segment(Start, Start.AddSeconds(5));
        var right = Segment(Start.AddSeconds(5), Start.AddSeconds(10));
        switch (changed)
        {
            case "session": right.SessionId = "other"; break;
            case "season": right.SeasonId = "64b000000000000000000002"; break;
            case "rate": right.EffectiveXpPerMinute++; break;
            case "channelMultiplier": right.ChannelMultiplier++; break;
            case "booster": right.AppliedServerBoosterMultiplier = 2m; break;
            case "settings": right.SettingsRevision++; break;
        }

        Assert.Equal(2, VoiceActivityAccumulator.Merge([left, right]).Count);
    }

    [Fact]
    public void New_session_rolls_to_next_part_at_segment_limit()
    {
        var day = new VoiceActivityDay
        {
            Id = "64b000000000000000000099", GuildId = 1, UserId = 2, DayStartUtc = Start, Part = 3, Revision = 9,
            Segments = [Segment(Start, Start.AddSeconds(5))], SessionCursors = [new VoiceSessionCursor { SessionId = "stable-session", ProcessedThroughUtc = Start.AddSeconds(5) }]
        };
        var input = Slice(Start.AddSeconds(10), Start.AddSeconds(15), 1m) with { SessionId = "new-session" };

        var plan = VoiceActivityAccumulator.Plan(day, input, maxSegmentsPerPart: 1);

        Assert.True(plan.RollOver);
        Assert.Equal(4, plan.Replacement!.Part);
        Assert.Single(plan.Replacement.Segments);
        Assert.Single(plan.Replacement.SessionCursors);
    }

    [Fact]
    public void Same_session_rolls_over_with_cursor_and_retries_use_the_new_part()
    {
        var day = new VoiceActivityDay
        {
            Id = "64b000000000000000000099", GuildId = 1, UserId = 2, DayStartUtc = Start, Part = 3, Revision = 9,
            Segments = [Segment(Start, Start.AddSeconds(5))], SessionCursors = [new VoiceSessionCursor { SessionId = "stable-session", ProcessedThroughUtc = Start.AddSeconds(5) }]
        };
        var input = Slice(Start.AddSeconds(5), Start.AddSeconds(10), 2m) with { ChannelId = 4 };

        var rollover = VoiceActivityAccumulator.Plan(day, input, maxSegmentsPerPart: 1);
        var duplicate = VoiceActivityAccumulator.Plan(rollover.Replacement, input, maxSegmentsPerPart: 1);
        var partial = VoiceActivityAccumulator.Plan(rollover.Replacement, Slice(Start.AddSeconds(8), Start.AddSeconds(12), 4m) with { ChannelId = 4 }, maxSegmentsPerPart: 2);

        Assert.True(rollover.RollOver);
        Assert.Equal(4, rollover.Replacement!.Part);
        Assert.Equal(Start.AddSeconds(10), rollover.Replacement.SessionCursors.Single().ProcessedThroughUtc);
        Assert.True(duplicate.Duplicate);
        Assert.True(partial.PartialOverlap);
        Assert.Equal(2, partial.Segment!.EligibleSeconds);
        Assert.Equal(0.333333m, partial.Segment.AwardedXp);
    }

    [Fact]
    public void Migration_does_not_modify_a_projecting_day()
    {
        var day = VoiceActivityAccumulator.Plan(null, Slice(Start, Start.AddSeconds(5), 1m)).Replacement!;
        day.ProjectionStatus = VoiceActivityProjectionStatus.Projecting;

        Assert.Throws<InvalidOperationException>(() => VoiceActivityAccumulator.Plan(day, Slice(Start.AddSeconds(5), Start.AddSeconds(10), 1m) with { AlreadyProjected = true }));
    }

    [Fact]
    public void Split_uses_unique_in_range_boundaries()
    {
        var result = VoiceActivityAccumulator.Split(Start, Start.AddMinutes(10), [Start.AddMinutes(-1), Start.AddMinutes(4), Start.AddMinutes(4), Start.AddMinutes(10)]);
        Assert.Equal([new(Start, Start.AddMinutes(4)), new(Start.AddMinutes(4), Start.AddMinutes(10))], result);
    }

    [Fact]
    public void Already_projected_migration_slice_snapshots_all_totals()
    {
        var day = VoiceActivityAccumulator.Plan(null, Slice(Start, Start.AddMinutes(1), 5m) with { AlreadyProjected = true }).Replacement!;
        Assert.Equal(VoiceActivityProjectionStatus.Applied, day.ProjectionStatus);
        Assert.Equal(day.ProjectionRevision, day.ProjectedRevision);
        Assert.Equal(day.TotalAwardedXp, day.ProjectedXp);
        Assert.Equal(day.TotalEligibleSeconds, day.ProjectedEligibleSeconds);
        Assert.Single(day.ProjectedSeasonTotals);
    }

    private static VoiceAccrualSlice Slice(DateTime start, DateTime end, decimal xp) => new(1, 2, "stable-session", start, end, 3,
        "64b000000000000000000001", (long)(end - start).TotalSeconds, xp, 10m, 1m, null, 7, end);

    private static VoiceActivitySegment Segment(DateTime start, DateTime end) => new()
    {
        SessionId = "stable-session", StartsAtUtc = start, EndsAtUtc = end, ChannelId = 3, SeasonId = "64b000000000000000000001",
        EligibleSeconds = (long)(end - start).TotalSeconds, AwardedXp = 1m, EffectiveXpPerMinute = 10m, ChannelMultiplier = 1m, SettingsRevision = 7
    };
}
