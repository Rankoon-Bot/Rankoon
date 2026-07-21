namespace Rankoon.Data.Discord;

/// <summary>Global watchdog settings; intended to become BotOwner-dashboard managed.</summary>
public sealed class VoiceWatchdogOptions
{
    public const string SectionName = "VoiceWatchdog";
    public int IntervalSeconds { get; set; } = 5;
}
