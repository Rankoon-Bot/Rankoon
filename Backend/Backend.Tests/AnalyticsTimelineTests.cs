using Rankoon.Data.Analytics;
using Rankoon.Data.Dashboard;
using Rankoon.Data.Model;
using Xunit;

namespace Backend.Tests;

public sealed class AnalyticsTimelineTests
{
    [Fact]
    public void Timeline_builds_gapless_half_open_buckets_and_aggregates_sources()
    {
        var start = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var generatedAt = start.AddDays(2).AddHours(12);
        var period = new GuildAnalyticsQueryService.TimelinePeriod("custom", start, generatedAt, GuildAnalyticsQueryService.TimelineBucketSize.Day);
        GuildAnalyticsQueryService.TimelineActivityRow[] rows =
        [
            new(1, start, "message", 1.25m, 0, 1),
            new(1, start.AddHours(2), "reaction", 0.75m, 0, 1),
            new(2, start.AddDays(2), "voice", 2.50m, 90, 1),
            new(3, start.AddDays(-1), "thread_create", 4m, 0, 2),
            new(9, generatedAt, "message", 100m, 0, 1)
        ];

        var result = GuildAnalyticsQueryService.BuildTimeline(period, generatedAt, rows, [start.AddDays(1), generatedAt]);

        Assert.Equal(3, result.Buckets.Count);
        Assert.All(result.Buckets.Zip(result.Buckets.Skip(1)), pair => Assert.Equal(pair.First.End, pair.Second.Start));
        Assert.Equal(2.00m, result.Buckets[0].AwardedXp.Total);
        Assert.Equal(0, result.Buckets[1].Activities.Total);
        Assert.Equal(90, result.Buckets[2].QualifiedVoiceSeconds);
        Assert.Equal(1, result.Buckets[2].ActiveVoiceMembers);
        Assert.True(result.Buckets[2].IsIncomplete);
        Assert.Equal(result.Buckets.Count, result.PreviousBuckets.Count);
        Assert.Equal(2, result.Summary.Current.ActiveMembers);
        Assert.Equal(3, result.Summary.Current.Activities.Total);
        Assert.Equal(4m, result.Summary.Previous.AwardedXp.Other);
        Assert.Equal(1, result.Summary.Current.LevelUps);
    }

    [Fact]
    public void Timeline_auto_bucket_and_custom_range_validation_are_bounded()
    {
        var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        var standard = GuildAnalyticsQueryService.CreateTimelinePeriod(new("90d", "auto", null, null), now);
        var last24Hours = GuildAnalyticsQueryService.CreateTimelinePeriod(new("24h", "day", null, null), now);
        var custom = GuildAnalyticsQueryService.CreateTimelinePeriod(new("custom", "day", now.AddDays(-180), now), now);

        Assert.Equal(GuildAnalyticsQueryService.TimelineBucketSize.Week, standard.BucketSize);
        Assert.Equal(TimeSpan.FromHours(24), last24Hours.End - last24Hours.Start);
        Assert.Equal(GuildAnalyticsQueryService.TimelineBucketSize.Day, custom.BucketSize);
        Assert.Throws<ArgumentException>(() => GuildAnalyticsQueryService.CreateTimelinePeriod(new("custom", "day", now.AddDays(-181), now), now));
    }

    [Fact]
    public void Dashboard_activity_uses_xp_share_and_exposes_trend_activity_count()
    {
        var start = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        var activity = DashboardOverviewService.BuildActivity(
            [
                new(1, start, "message", 9m, 0),
                new(2, start.AddHours(1), "voice", 1m, 60),
                new(3, start.AddHours(2), "voice", 0m, 30)
            ], start, 1, Array.Empty<ReportEvent>());

        Assert.Equal(90d, activity.Sources.Single(x => x.Source == "message").Percentage);
        Assert.Equal(10d, activity.Sources.Single(x => x.Source == "voice").Percentage);
        Assert.Equal(3, Assert.Single(activity.Trend).QualifiedActivityCount);
    }
}
