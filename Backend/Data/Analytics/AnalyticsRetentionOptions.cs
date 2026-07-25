namespace Rankoon.Data.Analytics;

public sealed class AnalyticsRetentionOptions
{
    public const string SectionName = "AnalyticsRetention";

    public int HourDays { get; set; } = 30;
    public int DayDays { get; set; } = 400;

    internal TimeSpan RetentionFor(Model.GuildAnalyticsGranularity granularity) => TimeSpan.FromDays(granularity switch
    {
        Model.GuildAnalyticsGranularity.Hour => Math.Clamp(HourDays, 1, 3_650),
        Model.GuildAnalyticsGranularity.Day => Math.Clamp(DayDays, 1, 3_650),
        _ => throw new ArgumentOutOfRangeException(nameof(granularity))
    });
}
