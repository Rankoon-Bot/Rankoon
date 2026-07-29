namespace Rankoon.Data.Analytics;

public sealed record AnalyticsTimelineQuery(string? Range, string? Bucket, DateTimeOffset? From, DateTimeOffset? To);

public sealed record AnalyticsTimelineMetricValues(decimal Total, decimal Voice, decimal Messages, decimal Reactions, decimal Other);

public sealed record AnalyticsTimelineActivityValues(long Total, long Voice, long Messages, long Reactions, long Other);

public sealed record AnalyticsTimelineAggregate(
    long ActiveMembers,
    long ActiveVoiceMembers,
    long QualifiedVoiceSeconds,
    AnalyticsTimelineMetricValues AwardedXp,
    AnalyticsTimelineActivityValues Activities,
    long LevelUps);

public sealed record AnalyticsTimelineSummary(AnalyticsTimelineAggregate Current, AnalyticsTimelineAggregate Previous);

public sealed record AnalyticsTimelineBucket(
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsIncomplete,
    long ActiveMembers,
    long ActiveVoiceMembers,
    long QualifiedVoiceSeconds,
    AnalyticsTimelineMetricValues AwardedXp,
    AnalyticsTimelineActivityValues Activities,
    long LevelUps);

public sealed record GuildAnalyticsTimelineResponse(
    DateTimeOffset RangeStart,
    DateTimeOffset RangeEnd,
    string TimeZone,
    string BucketSize,
    DateTimeOffset GeneratedAt,
    AnalyticsTimelineSummary Summary,
    IReadOnlyList<AnalyticsTimelineBucket> Buckets,
    IReadOnlyList<AnalyticsTimelineBucket> PreviousBuckets);
