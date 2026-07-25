using System.Globalization;
using System.Threading.Channels;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Analytics;
using Rankoon.Data.Operations;

namespace Rankoon.Data.Reporting;

public interface IReportWriter
{
    Task WriteAsync(ReportWrite report, CancellationToken cancellationToken = default);
}

public sealed class ReportWriter(RankoonDbContext database, TimeProvider timeProvider, IGuildAuditWriter audit, IGuildAnalyticsRecorder analytics, ILogger<ReportWriter> logger) : BackgroundService, IReportWriter
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private const int MaxMetadataEntries = 12;
    private const int MaxMetadataValueLength = 160;
    private readonly Channel<ReportEvent> _queue = Channel.CreateBounded<ReportEvent>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private static readonly HashSet<string> AllowedMetadata = new(StringComparer.Ordinal)
    {
        "amount", "channelId", "command", "count", "enabled", "errorType", "eventId", "hubId",
        "imported", "format", "skippedInvalid", "skippedForeignGuild", "duplicateUsers", "memberId", "source", "state", "targetId", "userId", "voiceChannelId", "seasonId", "sequence", "grantKey"
    };

    public Task WriteAsync(ReportWrite report, CancellationToken cancellationToken = default)
    {
        if (report.Name == ReportNames.XpGranted) return Task.CompletedTask;
        if (report.GuildId == 0 || !IsCategory(report.Category) || !IsToken(report.Name) || !IsToken(report.Outcome)) return Task.CompletedTask;
        var outcome = report.Outcome == ReportOutcomes.Succeeded ? GuildAnalyticsOutcome.Succeeded : report.Outcome == ReportOutcomes.Failed ? GuildAnalyticsOutcome.Failed : report.Outcome == ReportOutcomes.Rejected ? GuildAnalyticsOutcome.Rejected : GuildAnalyticsOutcome.Skipped;
        var feature = Feature(report.Name, report.Category);
        analytics.TryRecord(new(report.GuildId, GuildAnalyticsMetric.EventCount, Feature: feature, Outcome: outcome, Operation: report.Name, Source: report.Action, ChannelId: report.ChannelId, DurationSeconds: (report.DurationMs ?? 0) / 1000d));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var document = new ReportEvent
        {
            GuildId = report.GuildId,
            Category = report.Category,
            Name = report.Name[..Math.Min(report.Name.Length, 80)],
            GroupKey = BuildGroupKey(report),
            Action = IsToken(report.Action) ? report.Action![..Math.Min(report.Action!.Length, 80)] : null,
            Outcome = report.Outcome[..Math.Min(report.Outcome.Length, 40)],
            Severity = IsSeverity(report.Severity) ? report.Severity : null,
            ActorId = report.ActorId,
            SubjectId = report.SubjectId,
            ChannelId = report.ChannelId,
            CorrelationId = NormalizeIdentifier(report.CorrelationId),
            DurationMs = report.DurationMs is null ? null : Math.Clamp(report.DurationMs.Value, 0, 86_400_000),
            Metadata = SanitizeMetadata(report.Metadata),
            OccurredAt = now,
            RecordedAt = now,
            ExpiresAt = now.Add(Retention)
        };
        if (!_queue.Writer.TryWrite(document)) logger.LogWarning("Reporting queue is full; dropping {Category} event for guild {GuildId}", report.Category, report.GuildId);
        if (report.Category != ReportCategories.Command)
            _ = WriteAuditSafelyAsync(new(report.GuildId, report.Category, feature.ToString(), report.Action ?? report.Name, report.Outcome, report.ActorId, report.SubjectId?.ToString(), report.ChannelId, report.CorrelationId, report.Metadata), cancellationToken);
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ReportEvent>(200);
        while (await _queue.Reader.WaitToReadAsync(stoppingToken))
        {
            batch.Clear();
            while (batch.Count < 200 && _queue.Reader.TryRead(out var item)) batch.Add(item);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try { await database.ReportEvents.InsertManyAsync(batch, new() { IsOrdered = false }, stoppingToken); break; }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception) when (attempt < 3)
                {
                    logger.LogWarning(exception, "Unable to persist a batch of {Count} report events; retry {Attempt}/3", batch.Count, attempt + 1);
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), timeProvider, stoppingToken);
                }
                catch (Exception exception) { logger.LogError(exception, "Dropping a report batch of {Count} events after three attempts", batch.Count); }
            }
        }
    }

    private static Dictionary<string, string> SanitizeMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata == null) return [];
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            if (result.Count == MaxMetadataEntries) break;
            if (!AllowedMetadata.Contains(key) || value == null) continue;
            var text = value switch
            {
                bool boolean => boolean ? "true" : "false",
                byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToString(value, CultureInfo.InvariantCulture),
                Enum enumValue => enumValue.ToString(),
                string stringValue => SafeToken(stringValue),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(text)) continue;
            result[key] = text[..Math.Min(text.Length, MaxMetadataValueLength)];
        }
        return result;
    }

    private static bool IsCategory(string value) => value is ReportCategories.Activity or ReportCategories.Command;
    private static string? SafeToken(string value)
    {
        var redacted = OperationalErrorSanitizer.Redact(value.Trim(), MaxMetadataValueLength);
        return redacted.Length > 0 && redacted.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':' or '/') ? redacted : null;
    }
    private static bool IsSeverity(string? value) => value is ReportSeverities.Info or ReportSeverities.Warning or ReportSeverities.Error or ReportSeverities.Critical;
    private static bool IsToken(string? value) => !string.IsNullOrWhiteSpace(value) && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    private static string NormalizeToken(string value)
    {
        var normalized = new string(value.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '.').ToArray()).Trim('.');
        return string.IsNullOrEmpty(normalized) ? "unknown" : normalized[..Math.Min(normalized.Length, 80)];
    }
    private static string? NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= 100 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') ? value : null;
    }
    private static string BuildGroupKey(ReportWrite report)
    {
        return report.Name;
    }

    private static GuildAnalyticsFeature Feature(string name, string category) => category == ReportCategories.Command ? GuildAnalyticsFeature.Commands
        : name.Contains("voice", StringComparison.Ordinal) ? GuildAnalyticsFeature.Voice
        : name.Contains("season", StringComparison.Ordinal) ? GuildAnalyticsFeature.Seasons
        : name.Contains("leaderboard", StringComparison.Ordinal) ? GuildAnalyticsFeature.Leaderboard
        : name.Contains("self", StringComparison.Ordinal) || name.Contains("role", StringComparison.Ordinal) ? GuildAnalyticsFeature.SelfRoles
        : name.Contains("xp", StringComparison.Ordinal) ? GuildAnalyticsFeature.Experience
        : GuildAnalyticsFeature.Discord;

    private async Task WriteAuditSafelyAsync(GuildAuditWrite value, CancellationToken cancellationToken)
    {
        try { await audit.WriteAsync(value, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogError(exception, "Unable to forward legacy report event {Action} to guild audit", value.Action); }
    }
}
