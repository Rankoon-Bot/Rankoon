namespace Rankoon.Data.Discord;

public sealed class VoiceActivityOptions
{
    public const string SectionName = "VoiceActivity";
    public int CheckpointIntervalSeconds { get; set; } = 30;
    public int MaximumCheckpointBatchSize { get; set; } = 250;
    public int MaximumBatchDelayMilliseconds { get; set; } = 1000;
    public int ProjectionIntervalSeconds { get; set; } = 30;
    public int MaximumSegmentsPerDocument { get; set; } = 2000;
    public int ProjectionBatchSize { get; set; } = 100;
    public int MigrationBatchSize { get; set; } = 500;
    public int MaxWriteAttempts { get; set; } = 8;
    public int RuntimeWatermarkIntervalSeconds { get; set; } = 5;
    public int MaximumRecoveryGapSeconds { get; set; } = 35;
    public static bool IsValid(VoiceActivityOptions value) => value.CheckpointIntervalSeconds is >= 10 and <= 300 && value.MaximumCheckpointBatchSize is >= 1 and <= 1000 && value.MaximumBatchDelayMilliseconds is >= 0 and <= 10000 && value.ProjectionIntervalSeconds is >= 10 and <= 300 && value.ProjectionBatchSize is >= 1 and <= 1000 && value.RuntimeWatermarkIntervalSeconds is >= 1 && value.RuntimeWatermarkIntervalSeconds < value.CheckpointIntervalSeconds && value.MaximumRecoveryGapSeconds >= value.CheckpointIntervalSeconds && value.MaximumSegmentsPerDocument is >= 1 and <= 10000 && value.MigrationBatchSize is >= 1 and <= 5000 && value.MaxWriteAttempts is >= 1 and <= 20;
}
