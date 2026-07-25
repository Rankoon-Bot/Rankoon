using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Operations;

public sealed record OperationalErrorWrite(
    Exception Exception,
    string Source,
    string Operation,
    OperationalSeverity Severity = OperationalSeverity.Error,
    ulong? GuildId = null,
    ulong? ActorUserId = null,
    ulong? ChannelId = null,
    string? Command = null,
    string? Route = null,
    string? Worker = null,
    string? CorrelationId = null,
    string? TraceId = null,
    string? Build = null,
    IReadOnlyDictionary<string, object?>? Context = null,
    DateTimeOffset? OccurredAt = null);

public interface IOperationalErrorRecorder
{
    long PersistenceFailureCount { get; }
    Task<bool> RecordAsync(OperationalErrorWrite error, CancellationToken cancellationToken = default);
}

public sealed class OperationalErrorRecorder : IOperationalErrorRecorder
{
    public const int MaxAffectedGuildIds = 50;
    private static readonly AsyncLocal<int> RecursionDepth = new();
    private readonly RankoonDbContext _database;
    private readonly TimeProvider _timeProvider;
    private readonly ReportingRetentionOptions _options;
    private readonly ILogger<OperationalErrorRecorder> _logger;
    private long _persistenceFailureCount;

    public OperationalErrorRecorder(RankoonDbContext database, TimeProvider timeProvider, IOptions<ReportingRetentionOptions> options, ILogger<OperationalErrorRecorder> logger)
    {
        _database = database;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public long PersistenceFailureCount => Interlocked.Read(ref _persistenceFailureCount);

    public async Task<bool> RecordAsync(OperationalErrorWrite error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error.Exception);
        if (RecursionDepth.Value != 0) return false;
        RecursionDepth.Value++;
        try
        {
            var exception = error.Exception;
            var now = _timeProvider.GetUtcNow();
            var occurredAt = error.OccurredAt?.ToUniversalTime() ?? now;
            var source = OperationalErrorSanitizer.Redact(error.Source, 120);
            var operation = OperationalErrorSanitizer.Redact(error.Operation, 120);
            var exceptionType = OperationalErrorSanitizer.Redact(exception.GetType().FullName ?? exception.GetType().Name, 240);
            var message = OperationalErrorSanitizer.Redact(exception.Message, OperationalErrorSanitizer.MaxMessageLength);
            var fingerprint = OperationalErrorSanitizer.CreateFingerprint(exceptionType, message, source, operation);
            var occurrence = new OperationalErrorOccurrence
            {
                Fingerprint = fingerprint,
                Severity = error.Severity,
                GuildId = error.GuildId,
                ActorUserId = error.ActorUserId,
                ChannelId = error.ChannelId,
                Source = source,
                Command = Optional(error.Command, 120),
                Route = Optional(error.Route, 240),
                Worker = Optional(error.Worker, 120),
                ExceptionType = exceptionType,
                Message = message,
                StackTrace = OperationalErrorSanitizer.Redact(exception.StackTrace, OperationalErrorSanitizer.MaxStackTraceLength),
                InnerException = Optional(OperationalErrorSanitizer.Redact(exception.InnerException?.ToString(), OperationalErrorSanitizer.MaxStackTraceLength), OperationalErrorSanitizer.MaxStackTraceLength),
                CorrelationId = Optional(error.CorrelationId, 128),
                TraceId = Optional(error.TraceId, 128),
                Build = Optional(error.Build, 120),
                Metadata = OperationalErrorSanitizer.RedactContext(error.Context),
                OccurredAtUtc = occurredAt.UtcDateTime,
                RecordedAtUtc = now.UtcDateTime,
                ExpiresAtUtc = occurredAt.Add(_options.OccurrenceRetention).UtcDateTime
            };

            var succeeded = await TryPersistAsync(() => _database.OperationalErrorOccurrences.InsertOneAsync(occurrence, cancellationToken: cancellationToken), cancellationToken);
            succeeded &= await TryPersistAsync(() => UpsertIncidentAsync(occurrence, now.UtcDateTime, cancellationToken), cancellationToken);
            return succeeded;
        }
        finally
        {
            RecursionDepth.Value--;
        }
    }

    private async Task UpsertIncidentAsync(OperationalErrorOccurrence occurrence, DateTime now, CancellationToken cancellationToken)
    {
        var guildIds = occurrence.GuildId is { } guildId ? new BsonArray { new BsonInt64(unchecked((long)guildId)) } : new BsonArray();
        var update = new PipelineUpdateDefinition<OperationalIncident>(new BsonDocument[]
        {
            new("$set", new BsonDocument
            {
                { "fingerprint", new BsonDocument("$ifNull", new BsonArray { "$fingerprint", occurrence.Fingerprint }) },
                { "source", occurrence.Source },
                { "title", occurrence.ExceptionType + ": " + occurrence.Message },
                { "severity", occurrence.Severity.ToString() },
                { "occurrence_count", new BsonDocument("$add", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$occurrence_count", 0 }), 1 }) },
                { "_affected_guild_ids", new BsonDocument("$slice", new BsonArray
                    {
                        new BsonDocument("$setUnion", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$affected_guild_ids", new BsonArray() }), guildIds }),
                        MaxAffectedGuildIds
                    }) },
                { "first_seen_at_utc", new BsonDocument("$ifNull", new BsonArray { "$first_seen_at_utc", occurrence.OccurredAtUtc }) },
                { "last_seen_at_utc", new BsonDocument("$max", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$last_seen_at_utc", occurrence.OccurredAtUtc }), occurrence.OccurredAtUtc }) },
                { "last_occurrence_id", new BsonDocument("$cond", new BsonArray { new BsonDocument("$gte", new BsonArray { occurrence.OccurredAtUtc, new BsonDocument("$ifNull", new BsonArray { "$last_seen_at_utc", DateTime.MinValue }) }), new BsonObjectId(ObjectId.Parse(occurrence.Id!)), new BsonDocument("$ifNull", new BsonArray { "$last_occurrence_id", BsonNull.Value }) }) },
                { "last_build", occurrence.Build is null ? BsonNull.Value : occurrence.Build },
                { "created_at_utc", new BsonDocument("$ifNull", new BsonArray { "$created_at_utc", now }) },
                { "updated_at_utc", now },
                // A recurrence means the underlying condition is active again, so a resolved incident reopens as New.
                { "status", new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$in", new BsonArray { "$status", new BsonArray { OperationalIncidentStatus.Resolved.ToString(), OperationalIncidentStatus.Ignored.ToString() } }),
                        OperationalIncidentStatus.New.ToString(),
                        new BsonDocument("$ifNull", new BsonArray { "$status", OperationalIncidentStatus.New.ToString() })
                    }) },
                { "resolved_at_utc", new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$in", new BsonArray { "$status", new BsonArray { OperationalIncidentStatus.Resolved.ToString(), OperationalIncidentStatus.Ignored.ToString() } }),
                        BsonNull.Value,
                        new BsonDocument("$ifNull", new BsonArray { "$resolved_at_utc", BsonNull.Value })
                    }) },
                { "acknowledged_at_utc", ClearedWhenClosed("$acknowledged_at_utc") },
                { "acknowledged_by", ClearedWhenClosed("$acknowledged_by") },
                { "resolved_by", ClearedWhenClosed("$resolved_by") },
                { "ignored_at_utc", ClearedWhenClosed("$ignored_at_utc") },
                { "ignored_by", ClearedWhenClosed("$ignored_by") },
                { "note", ClearedWhenClosed("$note") },
                { "expires_at_utc", new BsonDocument("$dateAdd", new BsonDocument { { "startDate", new BsonDocument("$max", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$last_seen_at_utc", occurrence.OccurredAtUtc }), occurrence.OccurredAtUtc }) }, { "unit", "millisecond" }, { "amount", (long)_options.IncidentRetention.TotalMilliseconds } }) },
                { "schema_version", 1 }
            }),
            new("$set", new BsonDocument
            {
                { "affected_guild_ids", "$_affected_guild_ids" },
                { "affected_guild_count", new BsonDocument("$size", "$_affected_guild_ids") }
            }),
            new("$unset", "_affected_guild_ids")
        });
        try
        {
            await _database.OperationalIncidents.UpdateOneAsync(x => x.Fingerprint == occurrence.Fingerprint, update,
                new UpdateOptions { IsUpsert = true }, cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // A concurrent first occurrence won the unique-fingerprint upsert; apply this occurrence to that incident.
            await _database.OperationalIncidents.UpdateOneAsync(x => x.Fingerprint == occurrence.Fingerprint, update,
                cancellationToken: cancellationToken);
        }
    }

    private async Task<bool> TryPersistAsync(Func<Task> persist, CancellationToken cancellationToken)
    {
        try
        {
            await persist();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _persistenceFailureCount);
            _logger.LogError(exception, "Operational error persistence failed; failure was not recursively recorded");
            return false;
        }
    }

    private static BsonDocument ClearedWhenClosed(string field) => new("$cond", new BsonArray
    {
        new BsonDocument("$in", new BsonArray { "$status", new BsonArray { OperationalIncidentStatus.Resolved.ToString(), OperationalIncidentStatus.Ignored.ToString() } }),
        BsonNull.Value,
        new BsonDocument("$ifNull", new BsonArray { field, BsonNull.Value })
    });

    private static string? Optional(string? value, int maxLength) => string.IsNullOrWhiteSpace(value)
        ? null
        : OperationalErrorSanitizer.Redact(value, maxLength);
}
