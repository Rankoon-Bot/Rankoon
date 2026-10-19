namespace Rankoon.Data.Xp;

public sealed class XpIntegrityOptions
{
    public const string SectionName = "XpIntegrity";
    public bool Enabled { get; set; }
    public bool CatchUpAnnouncements { get; set; }
    public int IntervalMinutes { get; set; } = 60;
    public int BatchSize { get; set; } = 100;
    public int MemberTimeoutSeconds { get; set; } = 90;
    public int MaxAnnouncementsPerGuildPerRun { get; set; } = 20;
    public static bool IsValid(XpIntegrityOptions value) => value.IntervalMinutes is >= 5 and <= 1440 &&
        value.BatchSize is >= 1 and <= 500 && value.MemberTimeoutSeconds is >= 10 and <= 300 && value.MaxAnnouncementsPerGuildPerRun is >= 1 and <= 100;
}
