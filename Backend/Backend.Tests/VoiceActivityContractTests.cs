using MongoDB.Bson;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class VoiceActivityContractTests
{
    [Fact]
    public void Public_models_keep_existing_bson_names_without_duplicates()
    {
        var day = new VoiceActivityDay
        {
            TotalEligibleSeconds = 60,
            TotalAwardedXp = 10,
            ProjectedRevision = 2,
            ProjectedEligibleSeconds = 40,
            ProjectedXp = 7,
            CreatedAtUtc = DateTime.UnixEpoch
        }.ToBsonDocument();
        var segment = new VoiceActivitySegment { AppliedServerBoosterMultiplier = 1.5m }.ToBsonDocument();
        var lifetime = new VoiceSeasonTotal { SeasonId = null, EligibleSeconds = 60, AwardedXp = 10, ProjectedEligibleSeconds = 40, ProjectedXp = 7 }.ToBsonDocument();

        Assert.Equal(60, day["eligible_seconds"].AsInt64);
        Assert.True(day.Contains("awarded_xp"));
        Assert.True(day.Contains("projected_awarded_xp"));
        Assert.True(day.Contains("created_at_utc"));
        Assert.True(segment.Contains("server_booster_multiplier"));
        Assert.True(lifetime["season_id"].IsBsonNull);
        Assert.True(lifetime.Contains("projected_eligible_seconds"));
        Assert.True(lifetime.Contains("projected_xp"));
    }

    [Fact]
    public void Accrual_display_name_is_public_but_not_persisted()
    {
        var slice = new VoiceAccrualSlice(1, 2, "session", DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(5), 3, null, 5, 1, 12, 1, null, 1, DateTime.UnixEpoch, "Member");

        Assert.Equal("Member", slice.DisplayName);
        Assert.DoesNotContain("DisplayName", slice.ToBsonDocument().Names);
        var accrue = Assert.Single(typeof(IVoiceActivityAccumulator).GetMethods());
        Assert.Equal(nameof(IVoiceActivityAccumulator.AccrueAsync), accrue.Name);
        Assert.Equal([typeof(VoiceAccrualSlice), typeof(CancellationToken)], accrue.GetParameters().Select(x => x.ParameterType));
    }

    [Fact]
    public void Technical_options_have_requested_defaults()
    {
        var options = new VoiceActivityOptions();

        Assert.Equal(30, options.CheckpointIntervalSeconds);
        Assert.Equal(30, options.ProjectionIntervalSeconds);
        Assert.Equal(5, options.RuntimeWatermarkIntervalSeconds);
        Assert.Equal(35, options.MaximumRecoveryGapSeconds);
        Assert.Equal(2000, options.MaximumSegmentsPerDocument);
        Assert.Equal(100, options.ProjectionBatchSize);
        Assert.Equal(500, options.MigrationBatchSize);
    }

    [Fact]
    public void Projection_cadence_uses_persisted_time_and_immediate_boundaries_bypass_it()
    {
        var now = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(VoiceActivityProjectionService.ProjectionDue(now, now.AddSeconds(-19), TimeSpan.FromSeconds(20), false));
        Assert.True(VoiceActivityProjectionService.ProjectionDue(now, now.AddSeconds(-20), TimeSpan.FromSeconds(20), false));
        Assert.True(VoiceActivityProjectionService.ProjectionDue(now, now, TimeSpan.FromSeconds(20), true));
        Assert.Equal(TimeSpan.FromSeconds(20), VoiceActivityProjectionRepairService.NextDelay(0, 100, 20));
    }

    [Fact]
    public void Active_projection_uses_the_configured_repair_batch_size()
    {
        Assert.Equal(17, VoiceActivityProjectionService.PendingBatchSize(new VoiceActivityOptions { ProjectionBatchSize = 17 }));
    }
}
