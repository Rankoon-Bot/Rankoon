namespace Rankoon.Data.Dashboard;

public enum DashboardPeriod { SevenDays, ThirtyDays }
public enum DashboardOperationalStatus { Healthy, Warning, Critical, Disabled, SetupRequired, Unknown }
public enum DashboardAttentionSeverity { Info, Warning, Critical }
public enum DashboardBotIdentityMode { Rankoon, Custom }

public sealed record DashboardOverviewResponse(DateTimeOffset GeneratedAtUtc, DashboardPeriod Period, DateTimeOffset PeriodStartUtc, DateTimeOffset PeriodEndUtc, DashboardGuildSummary Guild, DashboardBotSummary Bot, DashboardHealthSummary Health, DashboardActivitySummary Activity, IReadOnlyList<DashboardModuleSummary> Modules, IReadOnlyList<DashboardRecentEvent> RecentEvents);
public sealed record DashboardGuildSummary(string GuildId, string Name, string? IconUrl, long MemberCount, long BotCount, int LiveVoiceMemberCount);
public sealed record DashboardBotSummary(DashboardBotIdentityMode IdentityMode, string? DisplayName, string? AvatarUrl, bool Connected, DashboardOperationalStatus Status, DateTimeOffset? LastReadyAtUtc, string? StatusReasonKey);
public sealed record DashboardHealthSummary(DashboardOperationalStatus OverallStatus, int HealthyModuleCount, int DisabledModuleCount, int SetupRequiredCount, int WarningCount, int CriticalCount, int UnknownCount, IReadOnlyList<DashboardAttentionItem> AttentionItems);
public sealed record DashboardAttentionItem(string Key, DashboardAttentionSeverity Severity, string ModuleId, string TitleKey, string DescriptionKey, IReadOnlyDictionary<string, string> Parameters, DashboardAttentionAction? Action);
public sealed record DashboardAttentionAction(string ActionKey, string ModuleId, string? ResourceId);
public sealed record DashboardActivitySummary(long ActiveMemberCount, decimal XpAwarded, long VoiceSeconds, long QualifiedActivityCount, long TemporaryChannelsCreated, DashboardComparisonSummary? Comparison, IReadOnlyList<DashboardActivitySource> Sources, IReadOnlyList<DashboardTrendPoint> Trend);
public sealed record DashboardComparisonSummary(DashboardMetricComparison ActiveMembers, DashboardMetricComparison XpAwarded, DashboardMetricComparison VoiceSeconds, DashboardMetricComparison QualifiedActivities);
public sealed record DashboardMetricComparison(decimal? PercentageChange, bool HasIncreaseFromZero);
public sealed record DashboardActivitySource(string Source, long EventCount, decimal XpAwarded, double Percentage);
public sealed record DashboardTrendPoint(DateTimeOffset DateUtc, decimal XpAwarded, long ActiveMemberCount, long VoiceSeconds);
public sealed record DashboardModuleSummary(string ModuleId, bool Enabled, DashboardOperationalStatus Status, string StatusReasonKey, IReadOnlyDictionary<string, string> StatusParameters, IReadOnlyList<DashboardModuleMetric> Metrics, DateTimeOffset? LastActivityAtUtc);
public sealed record DashboardModuleMetric(string Key, string Value);
public sealed record DashboardRecentEvent(string Id, string Name, string Outcome, string? Severity, string? ModuleId, string? ActorId, string? ActorDisplayName, string? SubjectId, string? SubjectDisplayName, string? ChannelId, string? ChannelName, IReadOnlyDictionary<string, string> Parameters, DateTimeOffset OccurredAtUtc);
