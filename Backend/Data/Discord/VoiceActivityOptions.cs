namespace Rankoon.Data.Discord;

public sealed class VoiceActivityOptions
{
    public const string SectionName = "VoiceActivity";
    public int ProjectionIntervalSeconds { get; set; } = 20;
    public int MaximumSegmentsPerDocument { get; set; } = 2000;
    public int ProjectionBatchSize { get; set; } = 100;
    public int MigrationBatchSize { get; set; } = 500;
    public int MaxWriteAttempts { get; set; } = 8;
}
