using System.Text.Json.Serialization;

namespace Rankoon.Data.Reporting;

public static class ReportCategories
{
    public const string Activity = "activity";
    public const string Command = "command";
    public const string Error = "error";
}

public static class ReportOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
}

public static class ReportSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
    public const string Critical = "critical";
}

public static class ReportNames
{
    public const string XpGranted = "xp.granted";
    public const string XpSettingsChanged = "xp.settings.changed";
    public const string VoiceWatchdogChanged = "voice.watchdog.changed";
    public const string Mee6Imported = "xp.mee6.imported";
    public const string LeaderboardSettingsChanged = "leaderboard.settings.changed";
    public const string LeaderboardPrivacyChanged = "leaderboard.privacy.changed";
    public const string RolePermissionsChanged = "permissions.roles.changed";
    public const string VoiceHubCreated = "voice.hub.created";
    public const string VoiceHubUpdated = "voice.hub.updated";
    public const string VoiceHubDeleted = "voice.hub.deleted";
    public const string VoiceChannelCreated = "voice.channel.created";
    public const string VoiceChannelDeleted = "voice.channel.deleted";
    public const string VoiceOwnershipTransferred = "voice.ownership.transferred";
    public const string XpProjectionRepaired = "xp.projection.repaired";
    public const string SeasonStarted = "season.started";
    public const string SeasonClosed = "season.closed";
    public const string SeasonCarryOverApplied = "season.carryover.applied";
}

public sealed record ReportWrite(
    ulong GuildId,
    string Category,
    string Name,
    string Outcome,
    string? Action = null,
    ulong? ActorId = null,
    long? DurationMs = null,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    string? Severity = null,
    ulong? SubjectId = null,
    ulong? ChannelId = null,
    string? CorrelationId = null);

public sealed class ReportQuery
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int Take { get; init; } = 50;
    public string? Cursor { get; init; }
    public string? Name { get; init; }
    public string? Action { get; init; }
    public string? Outcome { get; init; }
    public string? Severity { get; init; }
    public string? ActorId { get; init; }
    public string? SubjectId { get; init; }
    public string? ChannelId { get; init; }
    public string? CorrelationId { get; init; }
    public string? Search { get; init; }
}

public sealed record ReportItem(
    string Id,
    string Category,
    string Name,
    string? Action,
    string Outcome,
    string? Severity,
    string? ActorId,
    string? SubjectId,
    string? ChannelId,
    string? CorrelationId,
    long? DurationMs,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset OccurredAt);

public sealed record ReportListResponse(IReadOnlyList<ReportItem> Items, string? NextCursor);
public sealed record ReportSummaryGroup(string Key, long Count);
public sealed record ReportMetricGroup(string Key, long Count, long Succeeded, long Failed, double? AverageDurationMs, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);
public sealed record ReportTrendPoint(DateTimeOffset Timestamp, long Total, long Succeeded, long Failed);
public sealed record ReportSummaryResponse(
    long Total,
    long Succeeded,
    long Failed,
    double? AverageDurationMs,
    long UniqueActors,
    long UniqueCommands,
    IReadOnlyList<ReportSummaryGroup> ByName,
    IReadOnlyList<ReportSummaryGroup> ByOutcome,
    IReadOnlyList<ReportMetricGroup> Groups,
    IReadOnlyList<ReportTrendPoint> Trend);

[JsonSerializable(typeof(ReportCursorPayload))]
internal sealed partial class ReportJsonContext : JsonSerializerContext;

internal sealed record ReportCursorPayload(ulong GuildId, string Filter, long FromTicks, long ToTicks, long OccurredAtTicks, string Id);
