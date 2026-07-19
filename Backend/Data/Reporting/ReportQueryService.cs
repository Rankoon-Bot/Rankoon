using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;
using Rankoon.Data.Auth;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Reporting;

public interface IReportQueryService
{
    Task<ReportListResponse> ListAsync(ulong guildId, string category, ReportQuery query, CancellationToken cancellationToken = default);
    Task<ReportSummaryResponse> SummarizeAsync(ulong guildId, string category, ReportQuery query, CancellationToken cancellationToken = default);
}

public sealed class ReportQueryService(RankoonDbContext database, IOptions<JwtSettings> jwtSettings, TimeProvider timeProvider) : IReportQueryService
{
    private static readonly TimeSpan MaximumRange = TimeSpan.FromDays(90);
    private readonly byte[] _cursorKey = Encoding.UTF8.GetBytes(jwtSettings.Value.SecretKey);

    public async Task<ReportListResponse> ListAsync(ulong guildId, string category, ReportQuery query, CancellationToken cancellationToken = default)
    {
        var cursor = string.IsNullOrWhiteSpace(query.Cursor) ? null : DecodeCursor(query.Cursor, guildId);
        var normalized = Normalize(guildId, category, query, cursor);
        if (cursor != null && cursor.Filter != normalized.Fingerprint) throw new ArgumentException("The report cursor is invalid or does not match this query.");
        var filter = BuildFilter(normalized);
        if (cursor != null)
        {
            var cursorId = ObjectId.Parse(cursor.Id);
            filter &= Builders<ReportEvent>.Filter.Or(
                Builders<ReportEvent>.Filter.Lt(x => x.OccurredAt, new DateTime(cursor.OccurredAtTicks, DateTimeKind.Utc)),
                Builders<ReportEvent>.Filter.And(
                    Builders<ReportEvent>.Filter.Eq(x => x.OccurredAt, new DateTime(cursor.OccurredAtTicks, DateTimeKind.Utc)),
                    Builders<ReportEvent>.Filter.Lt("_id", cursorId)));
        }

        var documents = await database.ReportEvents.Find(filter)
            .Sort(Builders<ReportEvent>.Sort.Descending(x => x.OccurredAt).Descending("_id"))
            .Limit(normalized.Take + 1).ToListAsync(cancellationToken);
        var hasMore = documents.Count > normalized.Take;
        if (hasMore) documents.RemoveAt(documents.Count - 1);
        var nextCursor = hasMore && documents.Count > 0 ? EncodeCursor(normalized, documents[^1]) : null;
        return new(documents.Select(ToItem).ToArray(), nextCursor);
    }

    public async Task<ReportSummaryResponse> SummarizeAsync(ulong guildId, string category, ReportQuery query, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(guildId, category, query);
        var filter = BuildFilter(normalized);
        var totalTask = database.ReportEvents.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        var succeededTask = database.ReportEvents.CountDocumentsAsync(filter & Builders<ReportEvent>.Filter.Eq(x => x.Outcome, ReportOutcomes.Succeeded), cancellationToken: cancellationToken);
        var failedTask = database.ReportEvents.CountDocumentsAsync(filter & Builders<ReportEvent>.Filter.Eq(x => x.Outcome, ReportOutcomes.Failed), cancellationToken: cancellationToken);
        var durationTask = database.ReportEvents.Aggregate().Match(filter & Builders<ReportEvent>.Filter.Ne(x => x.DurationMs, null))
            .Group(_ => 1, group => new { Average = group.Average(x => x.DurationMs) }).FirstOrDefaultAsync(cancellationToken);
        var namesTask = database.ReportEvents.Aggregate().Match(filter)
            .Group(x => x.Name, group => new { Key = group.Key, Count = group.Count() })
            .SortByDescending(x => x.Count).ThenBy(x => x.Key).Limit(20).ToListAsync(cancellationToken);
        var outcomesTask = database.ReportEvents.Aggregate().Match(filter)
            .Group(x => x.Outcome, group => new { Key = group.Key, Count = group.Count() })
            .SortByDescending(x => x.Count).ThenBy(x => x.Key).ToListAsync(cancellationToken);
        var groupsTask = database.ReportEvents.Aggregate().Match(filter)
            .Group(x => x.GroupKey, group => new
            {
                Key = group.Key,
                Count = group.Count(),
                Succeeded = group.Sum(x => x.Outcome == ReportOutcomes.Succeeded ? 1 : 0),
                Failed = group.Sum(x => x.Outcome == ReportOutcomes.Failed ? 1 : 0),
                AverageDurationMs = group.Average(x => x.DurationMs),
                FirstSeenAt = group.Min(x => x.OccurredAt),
                LastSeenAt = group.Max(x => x.OccurredAt)
            }).SortByDescending(x => x.Count).ThenBy(x => x.Key).Limit(20).ToListAsync(cancellationToken);
        var uniqueActorsTask = database.ReportEvents.Distinct(x => x.ActorId, filter & Builders<ReportEvent>.Filter.Ne(x => x.ActorId, null)).ToListAsync(cancellationToken);
        var uniqueCommandsTask = database.ReportEvents.Distinct<string>("metadata.command", filter & Builders<ReportEvent>.Filter.Exists("metadata.command")).ToListAsync(cancellationToken);
        var trendTask = database.ReportEvents.Aggregate()
            .Match(filter)
            .AppendStage<BsonDocument>(new BsonDocument("$group", new BsonDocument
            {
                { "_id", new BsonDocument("$dateToString", new BsonDocument { { "format", "%Y-%m-%d" }, { "date", "$occurred_at" }, { "timezone", "UTC" } }) },
                { "total", new BsonDocument("$sum", 1) },
                { "succeeded", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$outcome", ReportOutcomes.Succeeded }), 1, 0 })) },
                { "failed", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$outcome", ReportOutcomes.Failed }), 1, 0 })) }
            })).Sort(new BsonDocument("_id", 1)).ToListAsync(cancellationToken);
        await Task.WhenAll(totalTask, succeededTask, failedTask, durationTask, namesTask, outcomesTask, groupsTask, uniqueActorsTask, uniqueCommandsTask, trendTask);
        var duration = await durationTask;
        var names = await namesTask;
        var outcomes = await outcomesTask;
        var groups = await groupsTask;
        var trend = await trendTask;
        return new(
            await totalTask,
            await succeededTask,
            await failedTask,
            duration?.Average,
            (await uniqueActorsTask).Count,
            (await uniqueCommandsTask).Count,
            names.Select(x => new ReportSummaryGroup(x.Key, x.Count)).ToArray(),
            outcomes.Select(x => new ReportSummaryGroup(x.Key, x.Count)).ToArray(),
            groups.Select(x => new ReportMetricGroup(x.Key, x.Count, x.Succeeded, x.Failed, x.AverageDurationMs,
                new DateTimeOffset(DateTime.SpecifyKind(x.FirstSeenAt, DateTimeKind.Utc)), new DateTimeOffset(DateTime.SpecifyKind(x.LastSeenAt, DateTimeKind.Utc)))).ToArray(),
            trend.Select(x => new ReportTrendPoint(
                new DateTimeOffset(DateTime.SpecifyKind(DateTime.ParseExact(x["_id"].AsString, "yyyy-MM-dd", null), DateTimeKind.Utc)),
                x["total"].ToInt64(), x["succeeded"].ToInt64(), x["failed"].ToInt64())).ToArray());
    }

    private NormalizedQuery Normalize(ulong guildId, string category, ReportQuery query, ReportCursorPayload? cursor = null)
    {
        if (guildId == 0) throw new ArgumentException("A guild is required.");
        if (category is not (ReportCategories.Activity or ReportCategories.Command or ReportCategories.Error)) throw new ArgumentException("Invalid report category.");
        var now = timeProvider.GetUtcNow();
        var to = cursor == null ? query.To?.ToUniversalTime() ?? now : new DateTimeOffset(cursor.ToTicks, TimeSpan.Zero);
        if (to > now) to = now;
        var from = cursor == null ? query.From?.ToUniversalTime() ?? to.Subtract(TimeSpan.FromDays(7)) : new DateTimeOffset(cursor.FromTicks, TimeSpan.Zero);
        if (from > to) throw new ArgumentException("'from' must be before 'to'.");
        if (to - from > MaximumRange) throw new ArgumentException("The date range cannot exceed 90 days.");
        var name = NormalizeFilter(query.Name, 80);
        var action = NormalizeFilter(query.Action, 80);
        var outcome = NormalizeFilter(query.Outcome, 40);
        var severity = NormalizeFilter(query.Severity, 20);
        var correlationId = NormalizeFilter(query.CorrelationId, 100);
        var search = NormalizeSearch(query.Search);
        var actorId = ParseOptionalId(query.ActorId, "actorId");
        var subjectId = ParseOptionalId(query.SubjectId, "subjectId");
        var channelId = ParseOptionalId(query.ChannelId, "channelId");
        var canonical = $"{category}|{from.UtcTicks}|{to.UtcTicks}|{name}|{action}|{outcome}|{severity}|{actorId}|{subjectId}|{channelId}|{correlationId}|{search}";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(guildId, category, from.UtcDateTime, to.UtcDateTime, Math.Clamp(query.Take, 1, 100), name, action, outcome, severity, actorId, subjectId, channelId, correlationId, search, fingerprint);
    }

    private static FilterDefinition<ReportEvent> BuildFilter(NormalizedQuery query)
    {
        var filter = Builders<ReportEvent>.Filter.Eq(x => x.GuildId, query.GuildId)
            & Builders<ReportEvent>.Filter.Eq(x => x.Category, query.Category)
            & Builders<ReportEvent>.Filter.Gte(x => x.OccurredAt, query.From)
            & Builders<ReportEvent>.Filter.Lte(x => x.OccurredAt, query.To);
        if (query.Name != null) filter &= Builders<ReportEvent>.Filter.Eq(x => x.Name, query.Name);
        if (query.Action != null) filter &= Builders<ReportEvent>.Filter.Eq(x => x.Action, query.Action);
        if (query.Outcome != null) filter &= Builders<ReportEvent>.Filter.Eq(x => x.Outcome, query.Outcome);
        if (query.Severity != null) filter &= Builders<ReportEvent>.Filter.Eq(x => x.Severity, query.Severity);
        if (query.ActorId != null) filter &= Builders<ReportEvent>.Filter.Eq(x => x.ActorId, query.ActorId);
        if (query.SubjectId != null) filter &= Builders<ReportEvent>.Filter.Eq(x => x.SubjectId, query.SubjectId);
        if (query.ChannelId != null) filter &= Builders<ReportEvent>.Filter.Eq(x => x.ChannelId, query.ChannelId);
        if (query.CorrelationId != null) filter &= Builders<ReportEvent>.Filter.Eq(x => x.CorrelationId, query.CorrelationId);
        if (query.Search != null)
        {
            var regex = new BsonRegularExpression(Regex.Escape(query.Search), "i");
            filter &= Builders<ReportEvent>.Filter.Or(
                Builders<ReportEvent>.Filter.Regex(x => x.Name, regex),
                Builders<ReportEvent>.Filter.Regex(x => x.Action, regex),
                Builders<ReportEvent>.Filter.Regex(x => x.CorrelationId, regex));
        }
        return filter;
    }

    private string EncodeCursor(NormalizedQuery query, ReportEvent item)
    {
        var cursor = new ReportCursorPayload(query.GuildId, query.Fingerprint, query.From.Ticks, query.To.Ticks, item.OccurredAt.Ticks, item.Id!);
        var payload = JsonSerializer.SerializeToUtf8Bytes(cursor, ReportJsonContext.Default.ReportCursorPayload);
        var signature = HMACSHA256.HashData(_cursorKey, payload);
        return $"{WebEncoders.Base64UrlEncode(payload)}.{WebEncoders.Base64UrlEncode(signature)}";
    }

    private ReportCursorPayload DecodeCursor(string cursor, ulong guildId)
    {
        try
        {
            if (cursor.Length > 1024) throw new FormatException();
            var parts = cursor.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var payloadBytes = WebEncoders.Base64UrlDecode(parts[0]);
            var signature = WebEncoders.Base64UrlDecode(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(_cursorKey, payloadBytes))) throw new FormatException();
            var payload = JsonSerializer.Deserialize(payloadBytes, ReportJsonContext.Default.ReportCursorPayload);
            if (payload == null || payload.GuildId != guildId || !ObjectId.TryParse(payload.Id, out _) || payload.FromTicks <= 0 || payload.ToTicks <= 0 || payload.OccurredAtTicks <= 0) throw new FormatException();
            return payload;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The report cursor is invalid or does not match this query.");
        }
    }

    private static string? NormalizeFilter(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().ToLowerInvariant();
        if (value.Length > maximumLength || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))) throw new ArgumentException("A report filter is invalid.");
        return value;
    }

    private static string? NormalizeSearch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > 80 || value.Any(char.IsControl)) throw new ArgumentException("The report search is invalid.");
        return value;
    }

    private static ulong? ParseOptionalId(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!ulong.TryParse(value, out var result) || result == 0) throw new ArgumentException($"The report {field} is invalid.");
        return result;
    }

    private static ReportItem ToItem(ReportEvent item) => new(item.Id!, item.Category, item.Name, item.Action, item.Outcome, item.Severity,
        item.ActorId?.ToString(), item.SubjectId?.ToString(), item.ChannelId?.ToString(), item.CorrelationId, item.DurationMs, item.Metadata,
        new DateTimeOffset(DateTime.SpecifyKind(item.OccurredAt, DateTimeKind.Utc)));
    private sealed record NormalizedQuery(ulong GuildId, string Category, DateTime From, DateTime To, int Take, string? Name, string? Action,
        string? Outcome, string? Severity, ulong? ActorId, ulong? SubjectId, ulong? ChannelId, string? CorrelationId, string? Search, string Fingerprint);
}
