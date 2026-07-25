using Discord.WebSocket;
using Microsoft.Extensions.Caching.Memory;
using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Data.Analytics;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Utils;

namespace Rankoon.Data.Operations;

public sealed record OperationsRange(string Key, DateTimeOffset From, DateTimeOffset To);
public sealed record OperationsMetric(string Code, decimal Value, decimal? PreviousValue, decimal? ChangePercent);
public sealed record OperationsTrendPoint(DateTimeOffset Timestamp, decimal Value, decimal? PreviousValue);
public sealed record OperationsInsight(string Code, string Severity, IReadOnlyDictionary<string, object?> Context);
public sealed record OperationsOverviewResponse(DateTimeOffset GeneratedAt, OperationsRange Range, IReadOnlyList<OperationsMetric> Metrics, IReadOnlyList<OperationsTrendPoint> Trend, IReadOnlyList<OperationsInsight> Insights, IReadOnlyList<UsageBreakdown>? Breakdown = null);
public sealed record OperationsGuild(string GuildId, string Name, string? IconUrl, int MemberCount, DateTimeOffset? BotJoinedAt, DateTimeOffset? LastActivityAt, long ActivityEventCount, long CommandEventCount, long ErrorEventCount, long FailedEventCount, long UniqueActorCount, long ActiveDayCount, decimal ActivityPerHundredMembers, string Status, IReadOnlyList<string>? ReasonCodes = null);
public sealed record GuildHealthResponse(DateTimeOffset GeneratedAt, OperationsRange Range, IReadOnlyList<OperationsMetric> Summary, IReadOnlyList<OperationsGuild> Guilds);
public sealed record UsageBreakdown(string Code, string? Label, decimal Value, decimal? PreviousValue);
public sealed record GlobalUsageResponse(DateTimeOffset GeneratedAt, OperationsRange Range, IReadOnlyList<OperationsMetric> Metrics, IReadOnlyList<OperationsTrendPoint> Trend, IReadOnlyList<UsageBreakdown> Breakdown);
public sealed record IncidentOccurrenceDto(string Id, DateTimeOffset OccurredAt, string Message, string? GuildId, string? GuildName, string? CorrelationId, string? StackTrace, string? Command = null, string? Route = null, string? Worker = null, string? BuildVersion = null);
public sealed record IncidentDimension(string Code, string? Label, long Count);
public sealed record IncidentFrequencyPoint(DateTimeOffset Timestamp, long Count);
public sealed record BotIncidentDto(string Id, string Code, string Title, string Source, string Severity, string Status, long OccurrenceCount, long AffectedGuildCount, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy, DateTimeOffset? ResolvedAt, string? ResolvedBy, string? Note, IReadOnlyList<IncidentOccurrenceDto> Occurrences, IReadOnlyDictionary<string, IReadOnlyList<IncidentDimension>>? Dimensions = null, IReadOnlyList<IncidentFrequencyPoint>? FrequencyTrend = null);
public sealed record IncidentResponse(DateTimeOffset GeneratedAt, IReadOnlyList<OperationsMetric> Summary, IReadOnlyList<BotIncidentDto> Items, string? NextCursor);
public sealed record IncidentQuery(string? Range, string? Status, string? Severity, string? Source, ulong? GuildId, DateTimeOffset? From, DateTimeOffset? To, string? Search, string? Sort, string? Direction, string? Cursor, int Limit = 30);
public sealed record IncidentTransitionRequest(string Action, string? Note);
public sealed record ErrorOccurrenceResponse(string Id, string Fingerprint, string Severity, string Source, string ExceptionType, string Message, string StackTrace, string? InnerException, string? GuildId, string? ActorUserId, string? ChannelId, string? Command, string? Route, string? Worker, string? CorrelationId, string? TraceId, string? Build, IReadOnlyDictionary<string, string> Metadata, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt);

public static class IncidentTransitions
{
    /// <summary>Resolved and ignored incidents reopen when the same fingerprint recurs.</summary>
    public static bool ReopensOnRecurrence(OperationalIncidentStatus status) => status is OperationalIncidentStatus.Resolved or OperationalIncidentStatus.Ignored;

    public static bool TryApply(OperationalIncidentStatus current, string action, out OperationalIncidentStatus next)
    {
        next = action.Trim().ToLowerInvariant() switch
        {
            "acknowledge" when current == OperationalIncidentStatus.New => OperationalIncidentStatus.Acknowledged,
            "resolve" when current is OperationalIncidentStatus.New or OperationalIncidentStatus.Acknowledged => OperationalIncidentStatus.Resolved,
            "ignore" when current is OperationalIncidentStatus.New or OperationalIncidentStatus.Acknowledged => OperationalIncidentStatus.Ignored,
            "reopen" when current is OperationalIncidentStatus.Resolved or OperationalIncidentStatus.Ignored => OperationalIncidentStatus.New,
            _ => current
        };
        return next != current;
    }
}

public interface IOperationsQueryService
{
    Task<OperationsOverviewResponse> OverviewAsync(AnalyticsRange range, CancellationToken token);
    Task<GuildHealthResponse> GuildHealthAsync(AnalyticsRange range, CancellationToken token);
    Task<GlobalUsageResponse> UsageAsync(AnalyticsRange range, CancellationToken token);
    Task<IncidentResponse> IncidentsAsync(IncidentQuery query, CancellationToken token);
    Task<BotIncidentDto?> IncidentAsync(string fingerprint, CancellationToken token);
    Task<BotIncidentDto?> TransitionAsync(string fingerprint, IncidentTransitionRequest request, ulong actorId, CancellationToken token);
    Task<ErrorOccurrenceResponse?> OccurrenceAsync(string id, CancellationToken token);
}

public sealed class OperationsQueryService(RankoonDbContext database, DiscordShardedClient discord, TimeProvider timeProvider, IMemoryCache cache, ISignedCursorService cursors, IGuildAnalyticsRecorder telemetry, IOperationalErrorRecorder errors, IWorkerHealthRegistry workers) : IOperationsQueryService
{
    public async Task<OperationsOverviewResponse> OverviewAsync(AnalyticsRange range, CancellationToken token)
    {
        var data = await UsageData(range, token); var open = await database.OperationalIncidents.CountDocumentsAsync(x => x.Status == OperationalIncidentStatus.New || x.Status == OperationalIncidentStatus.Acknowledged, cancellationToken: token);
        var critical = await database.OperationalIncidents.CountDocumentsAsync(x => (x.Status == OperationalIncidentStatus.New || x.Status == OperationalIncidentStatus.Acknowledged) && x.Severity == OperationalSeverity.Critical, cancellationToken: token);
        var errorGuilds = await database.OperationalErrorOccurrences.Aggregate().Match(x => x.OccurredAtUtc >= data.Range.From.UtcDateTime && x.OccurredAtUtc <= data.Range.To.UtcDateTime && x.GuildId != null).Group(x => x.GuildId, x => new GuildErrorAggregate(x.Key, x.LongCount())).Count().FirstOrDefaultAsync(token);
        var health = workers.GetSnapshot(TimeSpan.FromMinutes(5));
        var connected = discord.Guilds.Count; var activeGuilds = data.Current.Select(x => x.GuildId).Distinct().Count(); var errorGuildCount = errorGuilds?.Count ?? 0;
        var metrics = data.Metrics.Concat([Metric("connectedGuilds", connected, null), Metric("memberSum", discord.Guilds.Sum(x => (decimal)x.MemberCount), null), Metric("activeGuilds", activeGuilds, data.Previous.Select(x => x.GuildId).Distinct().Count()), Metric("errorGuilds", errorGuildCount, null), Metric("openIncidents", open, null), Metric("openCriticalIncidents", critical, null), Metric("errorFreeRate", connected == 0 ? 100 : decimal.Round((connected - errorGuildCount) * 100m / connected, 2), null), Metric("globalActivity", data.Current.Sum(x => x.Count), data.Previous.Sum(x => x.Count)), Metric("telemetryDrops", telemetry.DroppedCount, null), Metric("databaseWriterFailures", errors.PersistenceFailureCount, null), Metric("unhealthyWorkers", health.Count(x => x.IsStale || x.State != WorkerHealthState.Healthy), null)]).ToArray();
        var insights = health.Where(x => x.IsStale || x.State != WorkerHealthState.Healthy).Take(1).Select(x => new OperationsInsight("workerHealth", x.State == WorkerHealthState.Unhealthy ? "critical" : "warning", new Dictionary<string, object?> { ["worker"] = x.Worker, ["state"] = x.State.ToString(), ["stale"] = x.IsStale })).Concat(errorGuildCount > 0 ? [new OperationsInsight("actionGuilds", "warning", new Dictionary<string, object?> { ["count"] = errorGuildCount })] : []).Concat(critical > 0 ? [new OperationsInsight("criticalIncidents", "critical", new Dictionary<string, object?> { ["count"] = critical })] : []).Take(3).ToArray();
        var breakdown = data.Breakdown.Concat([new UsageBreakdown("gateway", discord.ConnectionState.ToString(), discord.ConnectionState == global::Discord.ConnectionState.Connected ? 1 : 0, null), new UsageBreakdown("build", typeof(OperationsQueryService).Assembly.GetName().Version?.ToString(), 1, null)]).ToArray();
        return new(data.Now, data.Range, metrics, data.Trend, insights, breakdown);
    }

    public async Task<GuildHealthResponse> GuildHealthAsync(AnalyticsRange range, CancellationToken token)
    {
        var now = timeProvider.GetUtcNow(); var period = GuildAnalyticsQueryService.CreatePeriod(range, now);
        var rows = await database.GuildAnalyticsBuckets.Aggregate().Match(x => x.Granularity == (range == AnalyticsRange.Last24Hours ? GuildAnalyticsGranularity.Hour : GuildAnalyticsGranularity.Day) && x.BucketStartUtc >= period.From.UtcDateTime && x.BucketStartUtc <= period.To.UtcDateTime)
            .Group(x => new { x.GuildId, x.Feature, x.Outcome }, x => new GuildAggregate(x.Key.GuildId, x.Key.Feature, x.Key.Outcome, x.Sum(y => y.Count), x.Max(y => y.BucketStartUtc))).Limit(100_000).ToListAsync(token);
        var occurrenceCounts = await database.OperationalErrorOccurrences.Aggregate().Match(x => x.OccurredAtUtc >= period.From.UtcDateTime && x.OccurredAtUtc <= period.To.UtcDateTime && x.GuildId != null).Group(x => x.GuildId, x => new GuildErrorAggregate(x.Key, x.LongCount())).ToListAsync(token);
        var byGuild = rows.GroupBy(x => x.GuildId).ToDictionary(x => x.Key, x => x.ToArray()); var errorByGuild = occurrenceCounts.ToDictionary(x => x.GuildId!.Value, x => x.Count);
        var connectedGuildIds = discord.Guilds.Select(guild => guild.Id).ToArray();
        var configuredGuilds = (await database.GuildXpSettings.Find(x => connectedGuildIds.Contains(x.GuildId)).Project(x => x.GuildId).ToListAsync(token)).ToHashSet();
        var guilds = discord.Guilds.Select(guild =>
        {
            var events = byGuild.GetValueOrDefault(guild.Id) ?? []; var count = events.Sum(x => x.Count); var commands = events.Where(x => x.Feature == GuildAnalyticsFeature.Commands).Sum(x => x.Count); var failed = events.Where(x => x.Outcome == GuildAnalyticsOutcome.Failed).Sum(x => x.Count); var errorCount = errorByGuild.GetValueOrDefault(guild.Id); var last = events.Length == 0 ? (DateTimeOffset?)null : Utc(events.Max(x => x.Last)); var intensity = guild.MemberCount == 0 ? 0 : decimal.Round(count * 100m / guild.MemberCount, 2);
            var reasons = new List<string>(); string status;
            if (errorCount > 0 || failed > 10) { status = "operationalErrors"; reasons.Add(errorCount > 0 ? "recentErrors" : "failedOperations"); }
            else if (guild.CurrentUser is { } bot && (!bot.GuildPermissions.ViewChannel || !bot.GuildPermissions.SendMessages)) { status = "permissionAttention"; reasons.Add("missingCorePermissions"); }
            else if (!configuredGuilds.Contains(guild.Id)) { status = "configurationAttention"; reasons.Add("xpNotConfigured"); }
            else if (guild.CurrentUser?.JoinedAt > now.AddDays(-7)) { status = "new"; reasons.Add("joinedRecently"); }
            else if (count == 0 && last is null) { status = "inactive"; reasons.Add("noRecordedActivity"); }
            else if (count < 5) { status = "lowActivity"; reasons.Add("lowEventVolume"); }
            else status = "healthy";
            return new OperationsGuild(guild.Id.ToString(), guild.Name, guild.IconUrl, guild.MemberCount, guild.CurrentUser?.JoinedAt, last, count - commands, commands, errorCount, failed, 0, events.Select(x => x.Last.Date).Distinct().LongCount(), intensity, status, reasons);
        }).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(now, new(period.Range, period.From, period.To), [Metric("guilds", guilds.Length, null), Metric("activeGuilds", guilds.Count(x => x.ActivityEventCount + x.CommandEventCount > 0), null), Metric("errors", guilds.Sum(x => x.ErrorEventCount), null)], guilds);
    }

    public async Task<GlobalUsageResponse> UsageAsync(AnalyticsRange range, CancellationToken token)
    {
        var data = await UsageData(range, token); return new(data.Now, data.Range, data.Metrics, data.Trend, data.Breakdown);
    }

    public async Task<IncidentResponse> IncidentsAsync(IncidentQuery query, CancellationToken token)
    {
        if (query.Limit is < 1 or > 100) throw new ArgumentException("Invalid incident query.");
        var now = timeProvider.GetUtcNow();
        DateTimeOffset from; DateTimeOffset to;
        if (query.From is not null || query.To is not null)
        {
            if (query.From is not { } customFrom || query.To is not { } customTo || customTo <= customFrom || customTo - customFrom > TimeSpan.FromDays(180) || customTo > now.AddMinutes(1)) throw new ArgumentException("Invalid incident period.");
            from = customFrom.ToUniversalTime(); to = customTo.ToUniversalTime();
        }
        else
        {
            if (!GuildAnalyticsQueryService.TryParseRange(query.Range, out var range)) throw new ArgumentException("Invalid incident range.");
            var period = GuildAnalyticsQueryService.CreatePeriod(range, now); from = period.From; to = period.To;
        }
        var sort = NormalizeSort(query.Sort);
        if (query.Direction is not null && !string.Equals(query.Direction, "asc", StringComparison.OrdinalIgnoreCase) && !string.Equals(query.Direction, "desc", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid sort direction.");
        var descending = !string.Equals(query.Direction, "asc", StringComparison.OrdinalIgnoreCase);
        var binding = $"incidents|{from:O}|{to:O}|{Normalize(query.Status)}|{Normalize(query.Severity)}|{Normalize(query.Source)}|{query.GuildId}|{Normalize(query.Search)}|{sort}|{descending}|{query.Limit}";
        var filter = Builders<OperationalIncident>.Filter.Gte(x => x.LastSeenAtUtc, from.UtcDateTime) & Builders<OperationalIncident>.Filter.Lte(x => x.LastSeenAtUtc, to.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(query.Status)) filter &= Builders<OperationalIncident>.Filter.Eq(x => x.Status, ParseStatus(query.Status));
        if (!string.IsNullOrWhiteSpace(query.Severity) && Enum.TryParse<OperationalSeverity>(query.Severity, true, out var severity)) filter &= Builders<OperationalIncident>.Filter.Eq(x => x.Severity, severity); else if (!string.IsNullOrWhiteSpace(query.Severity)) throw new ArgumentException("Invalid severity.");
        if (!string.IsNullOrWhiteSpace(query.Source)) filter &= Builders<OperationalIncident>.Filter.Eq(x => x.Source, NormalizeToken(query.Source, 120));
        if (query.GuildId is { } guildId) filter &= Builders<OperationalIncident>.Filter.AnyEq(x => x.AffectedGuildIds, guildId);
        if (!string.IsNullOrWhiteSpace(query.Search)) { if (query.Search.Length > 100) throw new ArgumentException("Search too long."); var regex = new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(query.Search.Trim()), "i"); filter &= Builders<OperationalIncident>.Filter.Or(Builders<OperationalIncident>.Filter.Regex(x => x.Title, regex), Builders<OperationalIncident>.Filter.Regex(x => x.Fingerprint, regex), Builders<OperationalIncident>.Filter.Regex(x => x.Source, regex)); }
        var summaryFilter = filter;
        if (query.Cursor != null) { if (!cursors.TryRead(query.Cursor, binding, out var cursor) || !ObjectId.TryParse(cursor.Id, out var cursorId)) throw new ArgumentException("Invalid cursor."); filter &= CursorFilter(sort, descending, cursor, cursorId); }
        var sortDefinition = Sort(sort, descending);
        var documents = await database.OperationalIncidents.Find(filter).Sort(sortDefinition).Limit(query.Limit + 1).ToListAsync(token); var page = documents.Take(query.Limit).ToArray(); var last = page.LastOrDefault();
        var next = documents.Count > query.Limit && last?.Id != null ? cursors.Create(Utc(last.LastSeenAtUtc), last.Id, binding, SortValue(last, sort)) : null;
        var summary = await database.OperationalIncidents.Aggregate().Match(summaryFilter).Group(x => x.Status, x => new StatusAggregate(x.Key, x.LongCount())).ToListAsync(token);
        return new(now, summary.Select(x => Metric(Status(x.Status), x.Count, null)).ToArray(), page.Select(x => Map(x, [])).ToArray(), next);
    }

    public async Task<BotIncidentDto?> IncidentAsync(string fingerprint, CancellationToken token)
    {
        var incident = await database.OperationalIncidents.Find(Identity(fingerprint)).FirstOrDefaultAsync(token); if (incident == null) return null;
        var occurrences = await database.OperationalErrorOccurrences.Find(x => x.Fingerprint == incident.Fingerprint).SortByDescending(x => x.OccurredAtUtc).Limit(20).ToListAsync(token);
        var dimensions = await database.OperationalErrorOccurrences.Aggregate().Match(x => x.Fingerprint == incident.Fingerprint)
            .Group(x => new { x.GuildId, x.Build, x.Command, x.Route, x.Worker }, x => new IncidentDimensionAggregate(x.Key.GuildId, x.Key.Build, x.Key.Command, x.Key.Route, x.Key.Worker, x.LongCount())).SortByDescending(x => x.Count).Limit(100).ToListAsync(token);
        var trendFrom = timeProvider.GetUtcNow().AddDays(-30).UtcDateTime;
        var trend = await database.OperationalErrorOccurrences.Aggregate().Match(x => x.Fingerprint == incident.Fingerprint && x.OccurredAtUtc >= trendFrom)
            .Group(x => x.OccurredAtUtc.Date, x => new IncidentTrendAggregate(x.Key, x.LongCount())).SortBy(x => x.At).ToListAsync(token);
        return Map(incident, occurrences.Select(x => Occurrence(x, true)).ToArray(), Dimensions(dimensions), trend.Select(x => new IncidentFrequencyPoint(Utc(x.At), x.Count)).ToArray());
    }

    public async Task<BotIncidentDto?> TransitionAsync(string fingerprint, IncidentTransitionRequest request, ulong actorId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(request.Action) || request.Note?.Length > 1000) throw new ArgumentException("Invalid transition.");
        var identity = Identity(fingerprint); var incident = await database.OperationalIncidents.Find(identity).FirstOrDefaultAsync(token); if (incident == null) return null;
        if (!IncidentTransitions.TryApply(incident.Status, request.Action, out var next)) throw new InvalidOperationException("Invalid transition.");
        var now = timeProvider.GetUtcNow().UtcDateTime; var update = Builders<OperationalIncident>.Update.Set(x => x.Status, next).Set(x => x.UpdatedAtUtc, now).Set(x => x.StatusNote, string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim());
        update = next switch { OperationalIncidentStatus.Acknowledged => update.Set(x => x.AcknowledgedAtUtc, now).Set(x => x.AcknowledgedBy, actorId), OperationalIncidentStatus.Resolved => update.Set(x => x.ResolvedAtUtc, now).Set(x => x.ResolvedBy, actorId), OperationalIncidentStatus.Ignored => update.Set(x => x.IgnoredAtUtc, now).Set(x => x.IgnoredBy, actorId), OperationalIncidentStatus.New => update.Unset(x => x.AcknowledgedAtUtc).Unset(x => x.AcknowledgedBy).Unset(x => x.ResolvedAtUtc).Unset(x => x.ResolvedBy).Unset(x => x.IgnoredAtUtc).Unset(x => x.IgnoredBy), _ => update };
        incident = await database.OperationalIncidents.FindOneAndUpdateAsync(identity & Builders<OperationalIncident>.Filter.Eq(x => x.Status, incident.Status), update, new FindOneAndUpdateOptions<OperationalIncident> { ReturnDocument = ReturnDocument.After }, token);
        return incident == null ? throw new InvalidOperationException("Incident changed concurrently.") : Map(incident, []);
    }

    public async Task<ErrorOccurrenceResponse?> OccurrenceAsync(string id, CancellationToken token)
    {
        if (!ObjectId.TryParse(id, out _)) return null; var value = await database.OperationalErrorOccurrences.Find(x => x.Id == id).FirstOrDefaultAsync(token); return value == null ? null : Map(value);
    }

    private async Task<UsageDataResult> UsageData(AnalyticsRange range, CancellationToken token)
    {
        var key = $"operations:usage:{range}"; if (cache.TryGetValue(key, out UsageDataResult? cached) && cached != null) return cached;
        var now = timeProvider.GetUtcNow(); var period = GuildAnalyticsQueryService.CreatePeriod(range, now); var granularity = range == AnalyticsRange.Last24Hours ? GuildAnalyticsGranularity.Hour : GuildAnalyticsGranularity.Day;
        var rows = await database.GuildAnalyticsBuckets.Aggregate().Match(x => x.Granularity == granularity && x.BucketStartUtc >= period.PreviousFrom.UtcDateTime && x.BucketStartUtc <= period.To.UtcDateTime).Group(x => new { x.BucketStartUtc, x.GuildId, x.Feature }, x => new UsageAggregate(x.Key.BucketStartUtc, x.Key.GuildId, x.Key.Feature, x.Sum(y => y.Count))).Limit(100_000).ToListAsync(token);
        var current = rows.Where(x => x.At >= period.From.UtcDateTime).ToArray(); var previous = rows.Where(x => x.At < period.From.UtcDateTime).ToArray(); var trend = current.GroupBy(x => x.At).OrderBy(x => x.Key).Select(x => new OperationsTrendPoint(Utc(x.Key), x.Sum(y => y.Count), previous.Where(y => y.At == x.Key - (period.To - period.From)).Sum(y => y.Count))).ToArray(); var breakdown = current.GroupBy(x => x.Feature).Select(x => new UsageBreakdown("adoption:" + x.Key, null, x.Select(y => y.GuildId).Distinct().Count(), previous.Where(y => y.Feature == x.Key).Select(y => y.GuildId).Distinct().Count())).OrderByDescending(x => x.Value).ToArray();
        var result = new UsageDataResult(now, new(period.Range, period.From, period.To), [Metric("events", current.Sum(x => x.Count), previous.Sum(x => x.Count)), Metric("adoptingGuilds", current.Select(x => x.GuildId).Distinct().Count(), previous.Select(x => x.GuildId).Distinct().Count())], trend, breakdown, current, previous); cache.Set(key, result, TimeSpan.FromSeconds(30)); return result;
    }

    private static BotIncidentDto Map(OperationalIncident x, IReadOnlyList<IncidentOccurrenceDto> occurrences, IReadOnlyDictionary<string, IReadOnlyList<IncidentDimension>>? dimensions = null, IReadOnlyList<IncidentFrequencyPoint>? trend = null) => new(x.Fingerprint, x.Fingerprint, x.Title, x.Source, x.Severity.ToString().ToLowerInvariant(), Status(x.Status), x.OccurrenceCount, x.AffectedGuildCount, Utc(x.FirstSeenAtUtc), Utc(x.LastSeenAtUtc), NullableUtc(x.AcknowledgedAtUtc), x.AcknowledgedBy?.ToString(), NullableUtc(x.ResolvedAtUtc), x.ResolvedBy?.ToString(), x.StatusNote, occurrences, dimensions, trend);
    private IncidentOccurrenceDto Occurrence(OperationalErrorOccurrence x, bool stack) => new(x.Id!, Utc(x.OccurredAtUtc), x.Message, x.GuildId?.ToString(), x.GuildId is { } id ? discord.GetGuild(id)?.Name : null, x.CorrelationId, stack ? x.StackTrace : null, x.Command, x.Route, x.Worker, x.Build);
    private static ErrorOccurrenceResponse Map(OperationalErrorOccurrence x) => new(x.Id!, x.Fingerprint, x.Severity.ToString().ToLowerInvariant(), x.Source, x.ExceptionType, x.Message, x.StackTrace, x.InnerException, x.GuildId?.ToString(), x.ActorUserId?.ToString(), x.ChannelId?.ToString(), x.Command, x.Route, x.Worker, x.CorrelationId, x.TraceId, x.Build, x.Metadata, Utc(x.OccurredAtUtc), Utc(x.RecordedAtUtc));
    private static FilterDefinition<OperationalIncident> Identity(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length > 128) return Builders<OperationalIncident>.Filter.Empty & Builders<OperationalIncident>.Filter.Where(_ => false);
        var filter = Builders<OperationalIncident>.Filter.Eq(x => x.Fingerprint, fingerprint);
        return ObjectId.TryParse(fingerprint, out _) ? filter | Builders<OperationalIncident>.Filter.Eq(x => x.Id, fingerprint) : filter;
    }
    private static string NormalizeSort(string? value) => value?.Trim().ToLowerInvariant() switch { null or "" or "lastseen" => "lastSeen", "count" => "count", "severity" => "severity", _ => throw new ArgumentException("Invalid incident sort.") };
    private static SortDefinition<OperationalIncident> Sort(string sort, bool descending)
    {
        var builder = Builders<OperationalIncident>.Sort;
        var primary = sort switch
        {
            "count" => descending ? builder.Descending(x => x.OccurrenceCount) : builder.Ascending(x => x.OccurrenceCount),
            // String enum order is Critical, Error, Warning, so ascending is the natural high-to-low severity order.
            "severity" => descending ? builder.Ascending(x => x.Severity) : builder.Descending(x => x.Severity),
            _ => descending ? builder.Descending(x => x.LastSeenAtUtc) : builder.Ascending(x => x.LastSeenAtUtc)
        };
        return builder.Combine(primary, descending ? builder.Descending("_id") : builder.Ascending("_id"));
    }
    private static FilterDefinition<OperationalIncident> CursorFilter(string sort, bool descending, SignedCursor cursor, ObjectId cursorId)
    {
        var builder = Builders<OperationalIncident>.Filter;
        FilterDefinition<OperationalIncident> primary;
        FilterDefinition<OperationalIncident> equal;
        switch (sort)
        {
            case "count" when long.TryParse(cursor.SortValue, out var count):
                primary = descending ? builder.Lt(x => x.OccurrenceCount, count) : builder.Gt(x => x.OccurrenceCount, count);
                equal = builder.Eq(x => x.OccurrenceCount, count);
                break;
            case "severity" when Enum.TryParse<OperationalSeverity>(cursor.SortValue, out var severity):
                primary = descending ? builder.Gt(x => x.Severity, severity) : builder.Lt(x => x.Severity, severity);
                equal = builder.Eq(x => x.Severity, severity);
                break;
            case "lastSeen":
                primary = descending ? builder.Lt(x => x.LastSeenAtUtc, cursor.Timestamp.UtcDateTime) : builder.Gt(x => x.LastSeenAtUtc, cursor.Timestamp.UtcDateTime);
                equal = builder.Eq(x => x.LastSeenAtUtc, cursor.Timestamp.UtcDateTime);
                break;
            default: throw new ArgumentException("Invalid incident cursor.");
        }
        var id = descending ? builder.Lt("_id", cursorId) : builder.Gt("_id", cursorId);
        return primary | (equal & id);
    }
    private static string SortValue(OperationalIncident incident, string sort) => sort switch { "count" => incident.OccurrenceCount.ToString(System.Globalization.CultureInfo.InvariantCulture), "severity" => incident.Severity.ToString(), _ => incident.LastSeenAtUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    private IReadOnlyDictionary<string, IReadOnlyList<IncidentDimension>> Dimensions(IEnumerable<IncidentDimensionAggregate> values)
    {
        var rows = values.ToArray();
        return new Dictionary<string, IReadOnlyList<IncidentDimension>>(StringComparer.Ordinal)
        {
            ["guilds"] = Top(rows.Where(x => x.GuildId != null).GroupBy(x => x.GuildId!.Value.ToString()).Select(x => new IncidentDimension(x.Key, discord.GetGuild(ulong.Parse(x.Key))?.Name, x.Sum(y => y.Count)))),
            ["builds"] = Top(rows.Where(x => x.Build != null).GroupBy(x => x.Build!).Select(x => new IncidentDimension(x.Key, null, x.Sum(y => y.Count)))),
            ["commands"] = Top(rows.Where(x => x.Command != null).GroupBy(x => x.Command!).Select(x => new IncidentDimension(x.Key, null, x.Sum(y => y.Count)))),
            ["routes"] = Top(rows.Where(x => x.Route != null).GroupBy(x => x.Route!).Select(x => new IncidentDimension(x.Key, null, x.Sum(y => y.Count)))),
            ["workers"] = Top(rows.Where(x => x.Worker != null).GroupBy(x => x.Worker!).Select(x => new IncidentDimension(x.Key, null, x.Sum(y => y.Count))))
        };
    }
    private static IReadOnlyList<IncidentDimension> Top(IEnumerable<IncidentDimension> values) => values.OrderByDescending(x => x.Count).ThenBy(x => x.Code, StringComparer.Ordinal).Take(20).ToArray();
    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
    private static string NormalizeToken(string value, int maximum) { var normalized = value.Trim().ToLowerInvariant(); if (normalized.Length is 0 || normalized.Length > maximum || normalized.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('.' or '_' or '-'))) throw new ArgumentException("Invalid token."); return normalized; }
    private static OperationalIncidentStatus ParseStatus(string value) => value.ToLowerInvariant() switch { "open" or "new" => OperationalIncidentStatus.New, "acknowledged" => OperationalIncidentStatus.Acknowledged, "resolved" => OperationalIncidentStatus.Resolved, "ignored" => OperationalIncidentStatus.Ignored, _ => throw new ArgumentException("Invalid status.") };
    private static string Status(OperationalIncidentStatus value) => value == OperationalIncidentStatus.New ? "open" : value.ToString().ToLowerInvariant();
    private static OperationsMetric Metric(string code, decimal value, decimal? previous) => new(code, value, previous, previous is null or 0 ? null : decimal.Round((value - previous.Value) * 100m / Math.Abs(previous.Value), 2));
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc)); private static DateTimeOffset? NullableUtc(DateTime? value) => value is null ? null : Utc(value.Value);
    private sealed record GuildAggregate(ulong GuildId, GuildAnalyticsFeature Feature, GuildAnalyticsOutcome Outcome, long Count, DateTime Last);
    private sealed record GuildErrorAggregate(ulong? GuildId, long Count); private sealed record StatusAggregate(OperationalIncidentStatus Status, long Count); private sealed record UsageAggregate(DateTime At, ulong GuildId, GuildAnalyticsFeature Feature, long Count);
    private sealed record IncidentDimensionAggregate(ulong? GuildId, string? Build, string? Command, string? Route, string? Worker, long Count);
    private sealed record IncidentTrendAggregate(DateTime At, long Count);
    private sealed record UsageDataResult(DateTimeOffset Now, OperationsRange Range, IReadOnlyList<OperationsMetric> Metrics, IReadOnlyList<OperationsTrendPoint> Trend, IReadOnlyList<UsageBreakdown> Breakdown, IReadOnlyList<UsageAggregate> Current, IReadOnlyList<UsageAggregate> Previous);
}
