namespace Rankoon.Data.Discord;

/// <summary>Global watchdog settings; intended to become BotOwner-dashboard managed.</summary>
public sealed class VoiceWatchdogOptions
{
    public const string SectionName = "VoiceWatchdog";
    private int reconciliationIntervalSeconds = 60;
    public int ReconciliationIntervalSeconds { get => reconciliationIntervalSeconds; set => reconciliationIntervalSeconds = value; }
    public int ShutdownTimeoutSeconds { get; set; } = 20;
    public int MaxConcurrentGuilds { get; set; } = 4;
    // A legacy five-second value represented the old full-settlement loop. Ignore it rather
    // than turning a modern 60-second reconciliation into an invalid configuration.
    public int IntervalSeconds { get => ReconciliationIntervalSeconds; set { if (value >= 10) ReconciliationIntervalSeconds = value; } }
    public static bool IsValid(VoiceWatchdogOptions value) => value.ReconciliationIntervalSeconds is >= 10 and <= 600 && value.ShutdownTimeoutSeconds is >= 1 and <= 120 && value.MaxConcurrentGuilds is >= 1 and <= 32;
}
