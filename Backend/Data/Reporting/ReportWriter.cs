using System.Globalization;
using System.Threading.Channels;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Reporting;

public interface IReportWriter
{
    Task WriteAsync(ReportWrite report, CancellationToken cancellationToken = default);
    Task WriteErrorAsync(ulong guildId, string source, Exception exception, ulong? actorId = null, IReadOnlyDictionary<string, object?>? metadata = null, CancellationToken cancellationToken = default);
}

public sealed class ReportWriter(RankoonDbContext database, TimeProvider timeProvider, ILogger<ReportWriter> logger) : BackgroundService, IReportWriter
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
        "imported", "memberId", "source", "state", "targetId", "userId", "voiceChannelId"
    };

    public Task WriteAsync(ReportWrite report, CancellationToken cancellationToken = default)
    {
        if (report.GuildId == 0 || !IsCategory(report.Category) || !IsToken(report.Name) || !IsToken(report.Outcome)) return Task.CompletedTask;
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
        return Task.CompletedTask;
    }

    public Task WriteErrorAsync(ulong guildId, string source, Exception exception, ulong? actorId = null, IReadOnlyDictionary<string, object?>? metadata = null, CancellationToken cancellationToken = default)
    {
        var values = metadata == null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(metadata);
        values["errorType"] = exception.GetBaseException().GetType().Name;
        return WriteAsync(new(guildId, ReportCategories.Error, NormalizeToken(source), ReportOutcomes.Failed, ActorId: actorId, Metadata: values,
            Severity: ReportSeverities.Error, ChannelId: ReadId(values, "channelId"), CorrelationId: ReadText(values, "eventId")), cancellationToken);
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
                string stringValue => stringValue,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(text)) continue;
            result[key] = text[..Math.Min(text.Length, MaxMetadataValueLength)];
        }
        return result;
    }

    private static bool IsCategory(string value) => value is ReportCategories.Activity or ReportCategories.Command or ReportCategories.Error;
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
    private static ulong? ReadId(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) && ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var id) ? id : null;
    private static string? ReadText(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    private static string BuildGroupKey(ReportWrite report)
    {
        if (report.Category != ReportCategories.Error || report.Metadata == null || !report.Metadata.TryGetValue("errorType", out var errorType)) return report.Name;
        var suffix = NormalizeToken(Convert.ToString(errorType, CultureInfo.InvariantCulture) ?? "unknown");
        return $"{report.Name}:{suffix}"[..Math.Min(report.Name.Length + suffix.Length + 1, 160)];
    }
}
