namespace Rankoon.Data.Operations;

public sealed class ReportingRetentionOptions
{
    public const string SectionName = "ReportingRetention";

    public int AuditDays { get; set; } = 180;
    public int OccurrenceDays { get; set; } = 30;
    public int IncidentDays { get; set; } = 180;

    internal TimeSpan AuditRetention => TimeSpan.FromDays(Math.Clamp(AuditDays, 1, 3_650));
    internal TimeSpan OccurrenceRetention => TimeSpan.FromDays(Math.Clamp(OccurrenceDays, 1, 3_650));
    internal TimeSpan IncidentRetention => TimeSpan.FromDays(Math.Clamp(IncidentDays, 1, 3_650));
}
