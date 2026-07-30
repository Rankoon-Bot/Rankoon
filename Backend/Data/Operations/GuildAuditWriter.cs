using System.Globalization;
using Microsoft.Extensions.Options;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Operations;

public sealed record GuildAuditWrite(
    ulong GuildId,
    string Type,
    string Feature,
    string Action,
    string Outcome,
    ulong? ActorUserId = null,
    string? SubjectId = null,
    ulong? ChannelId = null,
    string? CorrelationId = null,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    DateTimeOffset? OccurredAt = null);

public interface IGuildAuditWriter
{
    Task WriteAsync(GuildAuditWrite auditEvent, CancellationToken cancellationToken = default);
}

public sealed class GuildAuditWriter(RankoonDbContext database, TimeProvider timeProvider, IOptions<ReportingRetentionOptions> options) : IGuildAuditWriter
{
    private const int MaxMetadataEntries = 16;
    private const int MaxMetadataValueLength = 256;
    private static readonly HashSet<string> AllowedMetadata = new(StringComparer.Ordinal)
    {
        "amount", "channelId", "command", "count", "durationMs", "enabled", "errorType", "eventId", "feature",
        "format", "grantKey", "guildId", "hubId", "imported", "mappingId", "memberId", "outcome", "panelId",
        "requestId", "roleId", "seasonId", "sequence", "source", "state", "targetId", "userId", "voiceChannelId",
        "oldRevision", "newRevision", "addedRoles", "removedRoles", "addedModules", "removedModules"
    };

    public Task WriteAsync(GuildAuditWrite auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(auditEvent.GuildId);
        var now = timeProvider.GetUtcNow();
        var occurredAt = auditEvent.OccurredAt?.ToUniversalTime() ?? now;
        var document = new GuildAuditEvent
        {
            GuildId = auditEvent.GuildId,
            Type = NormalizeToken(auditEvent.Type, 80),
            Feature = NormalizeToken(auditEvent.Feature, 80),
            Action = NormalizeToken(auditEvent.Action, 80),
            Outcome = NormalizeToken(auditEvent.Outcome, 40),
            ActorUserId = auditEvent.ActorUserId,
            SubjectId = string.IsNullOrWhiteSpace(auditEvent.SubjectId) ? null : Bound(auditEvent.SubjectId, 128),
            ChannelId = auditEvent.ChannelId,
            CorrelationId = Bound(auditEvent.CorrelationId, 128),
            Metadata = SanitizeMetadata(auditEvent.Metadata),
            OccurredAtUtc = occurredAt.UtcDateTime,
            RecordedAtUtc = now.UtcDateTime,
            ExpiresAtUtc = occurredAt.Add(options.Value.AuditRetention).UtcDateTime
        };
        return database.GuildAuditEvents.InsertOneAsync(document, cancellationToken: cancellationToken);
    }

    public static Dictionary<string, string> SanitizeMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null) return [];
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            if (result.Count >= MaxMetadataEntries) break;
            if (!AllowedMetadata.Contains(key) || value is null) continue;
            var text = value switch
            {
                string item => SafeToken(item),
                bool item => item ? "true" : "false",
                Enum item => item.ToString(),
                byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToString(value, CultureInfo.InvariantCulture),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text)) result[key] = Bound(text, MaxMetadataValueLength);
        }
        return result;
    }

    private static string NormalizeToken(string value, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = new string(value.Trim().ToLowerInvariant().Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '.').ToArray()).Trim('.');
        if (normalized.Length == 0) throw new ArgumentException("Value must contain a token character.", nameof(value));
        return normalized[..Math.Min(normalized.Length, maxLength)];
    }

    private static string? SafeToken(string value)
    {
        var redacted = OperationalErrorSanitizer.Redact(value.Trim(), MaxMetadataValueLength);
        if (redacted.Length == 0 || redacted.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or ':' or '/'))) return null;
        return redacted;
    }

    private static string Bound(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];
}
