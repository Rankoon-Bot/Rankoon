using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace Rankoon.Data.Model;

public sealed class GuildAuditEvent
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("type")] public string Type { get; set; } = string.Empty;
    [BsonElement("feature")] public string Feature { get; set; } = string.Empty;
    [BsonElement("action")] public string Action { get; set; } = string.Empty;
    [BsonElement("outcome")] public string Outcome { get; set; } = string.Empty;
    [BsonElement("actor_user_id"), BsonIgnoreIfNull] public ulong? ActorUserId { get; set; }
    [BsonElement("subject_id"), BsonIgnoreIfNull] public string? SubjectId { get; set; }
    [BsonElement("channel_id"), BsonIgnoreIfNull] public ulong? ChannelId { get; set; }
    [BsonElement("correlation_id")] public string CorrelationId { get; set; } = string.Empty;
    [BsonElement("metadata")] public Dictionary<string, string> Metadata { get; set; } = [];
    [BsonElement("occurred_at_utc")] public DateTime OccurredAtUtc { get; set; }
    [BsonElement("recorded_at_utc")] public DateTime RecordedAtUtc { get; set; }
    [BsonElement("expires_at_utc")] public DateTime ExpiresAtUtc { get; set; }
    [BsonElement("schema_version")] public int SchemaVersion { get; set; } = 1;
}

public enum GuildAnalyticsGranularity
{
    Hour,
    Day
}

public enum GuildAnalyticsMetric
{
    EventCount,
    SuccessCount,
    FailureCount,
    DurationMilliseconds,
    Quantity
}

public enum GuildAnalyticsFeature
{
    Unspecified,
    Commands,
    Experience,
    Leaderboard,
    Seasons,
    Voice,
    SelfRoles,
    Authorization,
    Discord,
    Operations
}

public enum GuildAnalyticsOutcome
{
    Unspecified,
    Succeeded,
    Failed,
    Rejected,
    Skipped
}

public sealed class GuildAnalyticsBucket
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    [BsonElement("granularity"), BsonRepresentation(BsonType.String)] public GuildAnalyticsGranularity Granularity { get; set; }
    [BsonElement("bucket_start_utc")] public DateTime BucketStartUtc { get; set; }
    [BsonElement("metric"), BsonRepresentation(BsonType.String)] public GuildAnalyticsMetric Metric { get; set; }
    [BsonElement("feature"), BsonRepresentation(BsonType.String)] public GuildAnalyticsFeature Feature { get; set; }
    [BsonElement("outcome"), BsonRepresentation(BsonType.String)] public GuildAnalyticsOutcome Outcome { get; set; }
    [BsonElement("operation")] public string Operation { get; set; } = string.Empty;
    [BsonElement("source")] public string Source { get; set; } = string.Empty;
    [BsonElement("reason")] public string Reason { get; set; } = string.Empty;
    [BsonElement("channel_id"), BsonIgnoreIfNull] public ulong? ChannelId { get; set; }
    [BsonElement("count")] public long Count { get; set; }
    [BsonElement("value")] public long Value { get; set; }
    [BsonElement("duration_seconds")] public double DurationSeconds { get; set; }
    [BsonElement("updated_at_utc")] public DateTime UpdatedAtUtc { get; set; }
    [BsonElement("expires_at_utc")] public DateTime ExpiresAtUtc { get; set; }
    [BsonElement("schema_version")] public int SchemaVersion { get; set; } = 1;
}

public enum OperationalIncidentStatus
{
    New,
    Acknowledged,
    Resolved,
    Ignored
}

public enum OperationalSeverity { Warning, Error, Critical }

public sealed class OperationalErrorOccurrence
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("fingerprint")] public string Fingerprint { get; set; } = string.Empty;
    [BsonElement("severity"), BsonRepresentation(BsonType.String)] public OperationalSeverity Severity { get; set; } = OperationalSeverity.Error;
    [BsonElement("guild_id"), BsonIgnoreIfNull] public ulong? GuildId { get; set; }
    [BsonElement("source")] public string Source { get; set; } = string.Empty;
    [BsonElement("actor_user_id"), BsonIgnoreIfNull] public ulong? ActorUserId { get; set; }
    [BsonElement("channel_id"), BsonIgnoreIfNull] public ulong? ChannelId { get; set; }
    [BsonElement("command"), BsonIgnoreIfNull] public string? Command { get; set; }
    [BsonElement("route"), BsonIgnoreIfNull] public string? Route { get; set; }
    [BsonElement("worker"), BsonIgnoreIfNull] public string? Worker { get; set; }
    [BsonElement("exception_type")] public string ExceptionType { get; set; } = string.Empty;
    [BsonElement("message")] public string Message { get; set; } = string.Empty;
    [BsonElement("stack_trace")] public string StackTrace { get; set; } = string.Empty;
    [BsonElement("inner_exception_summary"), BsonIgnoreIfNull] public string? InnerException { get; set; }
    [BsonElement("correlation_id"), BsonIgnoreIfNull] public string? CorrelationId { get; set; }
    [BsonElement("trace_identifier"), BsonIgnoreIfNull] public string? TraceId { get; set; }
    [BsonElement("build_version"), BsonIgnoreIfNull] public string? Build { get; set; }
    [BsonElement("metadata")] public Dictionary<string, string> Metadata { get; set; } = [];
    [BsonElement("occurred_at_utc")] public DateTime OccurredAtUtc { get; set; }
    [BsonElement("recorded_at_utc")] public DateTime RecordedAtUtc { get; set; }
    [BsonElement("expires_at_utc")] public DateTime ExpiresAtUtc { get; set; }
    [BsonElement("schema_version")] public int SchemaVersion { get; set; } = 1;
}

public sealed class OperationalIncident
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator)), BsonRepresentation(BsonType.ObjectId)] public string? Id { get; set; }
    [BsonElement("fingerprint")] public string Fingerprint { get; set; } = string.Empty;
    [BsonElement("title")] public string Title { get; set; } = string.Empty;
    [BsonElement("status"), BsonRepresentation(BsonType.String)] public OperationalIncidentStatus Status { get; set; } = OperationalIncidentStatus.New;
    [BsonElement("source")] public string Source { get; set; } = string.Empty;
    [BsonElement("severity"), BsonRepresentation(BsonType.String)] public OperationalSeverity Severity { get; set; } = OperationalSeverity.Error;
    [BsonElement("occurrence_count")] public long OccurrenceCount { get; set; }
    [BsonElement("affected_guild_ids")] public List<ulong> AffectedGuildIds { get; set; } = [];
    [BsonElement("affected_guild_count")] public long AffectedGuildCount { get; set; }
    [BsonElement("first_seen_at_utc")] public DateTime FirstSeenAtUtc { get; set; }
    [BsonElement("last_seen_at_utc")] public DateTime LastSeenAtUtc { get; set; }
    [BsonElement("last_occurrence_id"), BsonIgnoreIfNull, BsonRepresentation(BsonType.ObjectId)] public string? LastOccurrenceId { get; set; }
    [BsonElement("last_build"), BsonIgnoreIfNull] public string? LastBuild { get; set; }
    [BsonElement("acknowledged_at_utc"), BsonIgnoreIfNull] public DateTime? AcknowledgedAtUtc { get; set; }
    [BsonElement("acknowledged_by"), BsonIgnoreIfNull] public ulong? AcknowledgedBy { get; set; }
    [BsonElement("resolved_at_utc"), BsonIgnoreIfNull] public DateTime? ResolvedAtUtc { get; set; }
    [BsonElement("resolved_by"), BsonIgnoreIfNull] public ulong? ResolvedBy { get; set; }
    [BsonElement("ignored_at_utc"), BsonIgnoreIfNull] public DateTime? IgnoredAtUtc { get; set; }
    [BsonElement("ignored_by"), BsonIgnoreIfNull] public ulong? IgnoredBy { get; set; }
    [BsonElement("note"), BsonIgnoreIfNull] public string? StatusNote { get; set; }
    [BsonElement("created_at_utc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updated_at_utc")] public DateTime UpdatedAtUtc { get; set; }
    [BsonElement("expires_at_utc")] public DateTime ExpiresAtUtc { get; set; }
    [BsonElement("schema_version")] public int SchemaVersion { get; set; } = 1;
}
