using Microsoft.Extensions.Caching.Memory;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Utils;
using Rankoon.Data.Discord;
using Rankoon.Data.Xp;

namespace Rankoon.Data.Analytics;

public enum AnalyticsRange { Last24Hours, Last7Days, Last30Days, Last90Days }
public sealed record AnalyticsPeriod(string Range, DateTimeOffset From, DateTimeOffset To, DateTimeOffset PreviousFrom, DateTimeOffset PreviousTo);
public sealed record AnalyticsKpi(string Code, decimal Value, decimal? PreviousValue, decimal? ChangePercent, string Direction);
public sealed record AnalyticsTrendPoint(DateTimeOffset Timestamp, decimal Value, decimal? PreviousValue);
public sealed record AnalyticsBreakdown(string Code, string? Label, decimal Value, decimal? PreviousValue, decimal? ChangePercent);
public sealed record AnalyticsInsight(string Code, string Severity, decimal? Value, IReadOnlyDictionary<string, object?> Context);
public abstract record GuildAnalyticsPageResponse(DateTimeOffset GeneratedAt, AnalyticsPeriod Period, IReadOnlyList<AnalyticsKpi> Kpis, IReadOnlyList<AnalyticsTrendPoint> Trend, IReadOnlyList<AnalyticsBreakdown> Breakdown, IReadOnlyList<AnalyticsInsight> Insights);
public sealed record GuildAnalyticsOverviewResponse(DateTimeOffset GeneratedAt, AnalyticsPeriod Period, IReadOnlyList<AnalyticsKpi> Kpis, IReadOnlyList<AnalyticsTrendPoint> Trend, IReadOnlyList<AnalyticsBreakdown> Breakdown, IReadOnlyList<AnalyticsInsight> Insights) : GuildAnalyticsPageResponse(GeneratedAt, Period, Kpis, Trend, Breakdown, Insights);
public sealed record GuildAnalyticsXpResponse(DateTimeOffset GeneratedAt, AnalyticsPeriod Period, IReadOnlyList<AnalyticsKpi> Kpis, IReadOnlyList<AnalyticsTrendPoint> Trend, IReadOnlyList<AnalyticsBreakdown> Breakdown, IReadOnlyList<AnalyticsInsight> Insights) : GuildAnalyticsPageResponse(GeneratedAt, Period, Kpis, Trend, Breakdown, Insights);
public sealed record GuildAnalyticsVoiceResponse(DateTimeOffset GeneratedAt, AnalyticsPeriod Period, IReadOnlyList<AnalyticsKpi> Kpis, IReadOnlyList<AnalyticsTrendPoint> Trend, IReadOnlyList<AnalyticsBreakdown> Breakdown, IReadOnlyList<AnalyticsInsight> Insights) : GuildAnalyticsPageResponse(GeneratedAt, Period, Kpis, Trend, Breakdown, Insights);
public sealed record GuildAnalyticsFeaturesResponse(DateTimeOffset GeneratedAt, AnalyticsPeriod Period, IReadOnlyList<AnalyticsKpi> Kpis, IReadOnlyList<AnalyticsTrendPoint> Trend, IReadOnlyList<AnalyticsBreakdown> Breakdown, IReadOnlyList<AnalyticsInsight> Insights) : GuildAnalyticsPageResponse(GeneratedAt, Period, Kpis, Trend, Breakdown, Insights);
public sealed record AnalyticsAuditItem(string Id, DateTimeOffset OccurredAt, string Code, string? ActorId, string? ActorName, string? SubjectId, string? SubjectName, string Outcome, string? CorrelationId, IReadOnlyDictionary<string, string> Metadata, string? ChannelId = null, string? ChannelName = null, string? HubName = null);
public sealed record GuildAnalyticsAuditResponse(DateTimeOffset GeneratedAt, AnalyticsPeriod Period, IReadOnlyList<AnalyticsKpi> Kpis, IReadOnlyList<AnalyticsTrendPoint> Trend, IReadOnlyList<AnalyticsBreakdown> Breakdown, IReadOnlyList<AnalyticsInsight> Insights, IReadOnlyList<AnalyticsAuditItem> Items, string? NextCursor) : GuildAnalyticsPageResponse(GeneratedAt, Period, Kpis, Trend, Breakdown, Insights);
public sealed record AnalyticsAuditQuery(string? Range, DateTimeOffset? From, DateTimeOffset? To, string? Type, string? Feature, string? Outcome, ulong? ActorUserId, ulong? ChannelId, string? Search, string? Cursor, int Limit = 50);

public interface IGuildAnalyticsQueryService
{
    Task<GuildAnalyticsOverviewResponse> OverviewAsync(ulong guildId, AnalyticsRange range, CancellationToken cancellationToken);
    Task<GuildAnalyticsXpResponse> XpAsync(ulong guildId, AnalyticsRange range, CancellationToken cancellationToken);
    Task<GuildAnalyticsVoiceResponse> VoiceAsync(ulong guildId, AnalyticsRange range, CancellationToken cancellationToken);
    Task<GuildAnalyticsFeaturesResponse> FeaturesAsync(ulong guildId, AnalyticsRange range, CancellationToken cancellationToken);
    Task<GuildAnalyticsTimelineResponse> TimelineAsync(ulong guildId, AnalyticsTimelineQuery query, CancellationToken cancellationToken);
    Task<GuildAnalyticsAuditResponse> AuditAsync(ulong guildId, AnalyticsAuditQuery query, CancellationToken cancellationToken);
}

public sealed class GuildAnalyticsQueryService(RankoonDbContext database, TimeProvider timeProvider, IMemoryCache cache, ISignedCursorService cursors, IGuildDiscordContextResolver discord, IGuildAnalyticsRecorder telemetry) : IGuildAnalyticsQueryService
{
    private const string ExperienceFeature = "experience";

    public static bool TryParseRange(string? value, out AnalyticsRange range)
    {
        range = value switch { "24h" => AnalyticsRange.Last24Hours, null or "" or "7d" => AnalyticsRange.Last7Days, "30d" => AnalyticsRange.Last30Days, "90d" => AnalyticsRange.Last90Days, _ => default };
        return value is null or "" or "24h" or "7d" or "30d" or "90d";
    }

    public static AnalyticsPeriod CreatePeriod(AnalyticsRange range, DateTimeOffset to)
    {
        var duration = range switch { AnalyticsRange.Last24Hours => TimeSpan.FromHours(24), AnalyticsRange.Last7Days => TimeSpan.FromDays(7), AnalyticsRange.Last30Days => TimeSpan.FromDays(30), AnalyticsRange.Last90Days => TimeSpan.FromDays(90), _ => throw new ArgumentOutOfRangeException(nameof(range)) };
        var from = to - duration;
        return new(Key(range), from, to, from - duration, from);
    }

    public async Task<GuildAnalyticsTimelineResponse> TimelineAsync(ulong guildId, AnalyticsTimelineQuery query, CancellationToken token)
    {
        var generatedAt = timeProvider.GetUtcNow();
        var period = CreateTimelinePeriod(query, generatedAt);
        var comparisonOffset = TimelineComparisonOffset(period);
        var previousStart = period.Start - comparisonOffset;
        var fromUtc = previousStart.UtcDateTime;
        var toUtc = period.End.UtcDateTime;
        var automaticGrant = Builders<XpLedgerEntry>.Filter.Eq(x => x.Kind, XpLedgerEntryKind.AutomaticGrant)
            | (Builders<XpLedgerEntry>.Filter.Eq(x => x.Kind, null) & Builders<XpLedgerEntry>.Filter.Eq(x => x.ReversesGrantKey, null));
        var ledgerFilter = Builders<XpLedgerEntry>.Filter.Eq(x => x.GuildId, guildId)
            & Builders<XpLedgerEntry>.Filter.Gte(x => x.OccurredAtUtc, fromUtc)
            & Builders<XpLedgerEntry>.Filter.Lt(x => x.OccurredAtUtc, toUtc)
            & Builders<XpLedgerEntry>.Filter.Eq(x => x.ProjectionStatus, SeasonProjectionStatus.Applied)
            & Builders<XpLedgerEntry>.Filter.Ne(x => x.CooldownDenied, true)
            & Builders<XpLedgerEntry>.Filter.Ne(x => x.IsProjectionControl, true)
            & Builders<XpLedgerEntry>.Filter.Gt(x => x.Amount, 0)
            & automaticGrant;
        var ledgerTask = database.XpLedger.Find(ledgerFilter)
            .Project(x => new TimelineLedgerDocument(x.UserId, x.OccurredAtUtc, x.Source, x.Amount, x.PeriodStartsAtUtc, x.PeriodEndsAtUtc))
            .ToListAsync(token);
        var voiceDaysTask = database.VoiceActivities.Find(x => x.GuildId == guildId && x.DayStartUtc >= fromUtc.Date && x.DayStartUtc <= toUtc.Date).ToListAsync(token);
        var migrationTask = database.VoiceLedgerMigrationStates.Find(x => x.Id == VoiceLedgerMigrationState.SingletonId).FirstOrDefaultAsync(token);
        var levelUpsTask = database.LevelTransitionEvents.Find(x => x.GuildId == guildId && x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc < toUtc && x.NewLevel > x.PreviousLevel)
            .Project(x => x.CreatedAtUtc).ToListAsync(token);
        await Task.WhenAll(ledgerTask, voiceDaysTask, migrationTask, levelUpsTask);

        var compressedVoiceAuthoritative = VoiceLedgerMigrationService.IsCompressedVoiceAuthoritative(migrationTask.Result);
        var rows = ledgerTask.Result.Where(x => x.Source != "voice" || !compressedVoiceAuthoritative)
            .Select(x => new TimelineActivityRow(x.UserId, Utc(x.OccurredAtUtc), x.Source, x.Amount, VoiceSeconds(x), 1L)).ToList();
        rows.AddRange(VoiceActivityReadModel.Select(voiceDaysTask.Result, compressedVoiceAuthoritative, fromUtc, toUtc)
            .Select(x => new TimelineActivityRow(x.UserId, Utc(x.OccurredAtUtc), "voice", x.AwardedXp, x.EligibleSeconds, 1L)));
        return BuildTimeline(period, generatedAt, rows, levelUpsTask.Result.Select(Utc).ToArray());
    }

    public Task<GuildAnalyticsOverviewResponse> OverviewAsync(ulong guildId, AnalyticsRange range, CancellationToken token) => CachedAsync<GuildAnalyticsOverviewResponse>($"analytics:overview:{guildId}:{range}", async () =>
    {
        var data = await ReadBucketsAsync(guildId, range, null, token);
        var voiceActivity = await ReadVoiceActivityAsync(guildId, data.Period, token);
        var participants = await ActiveRecipients(guildId, data.Period.From, data.Period.To, token);
        var previousParticipants = await ActiveRecipients(guildId, data.Period.PreviousFrom, data.Period.PreviousTo, token);
        var xp = data.Rows.Where(x => x.Operation == "xp.grant" && x.Metric == GuildAnalyticsMetric.Quantity).Sum(x => x.Value);
        var previousXp = data.PreviousRows.Where(x => x.Operation == "xp.grant" && x.Metric == GuildAnalyticsMetric.Quantity).Sum(x => x.Value);
        var voice = voiceActivity.Current.Sum(x => x.EligibleSeconds);
        var previousVoice = voiceActivity.Previous.Sum(x => x.EligibleSeconds);
        var levelUps = await LevelUps(guildId, data.Period.From, data.Period.To, token);
        var previousLevelUps = await LevelUps(guildId, data.Period.PreviousFrom, data.Period.PreviousTo, token);
        var insights = data.Insights.Concat(telemetry.DroppedCount > 0 ? [new AnalyticsInsight("telemetryDrops", "warning", telemetry.DroppedCount, new Dictionary<string, object?> { ["scope"] = "processLifetime" })] : []).Take(3).ToArray();
        return new(data.Now, data.Period, [Kpi("activeParticipants", participants, previousParticipants), Kpi("xpAwarded", xp, previousXp), Kpi("qualifiedVoiceSeconds", (decimal)voice, (decimal)previousVoice), Kpi("levelUps", levelUps, previousLevelUps)], data.Trend, data.Breakdown, insights);
    });

    public Task<GuildAnalyticsVoiceResponse> VoiceAsync(ulong guildId, AnalyticsRange range, CancellationToken token) => CachedAsync<GuildAnalyticsVoiceResponse>($"analytics:voice:{guildId}:{range}", async () =>
    {
        var data = await ReadBucketsAsync(guildId, range, GuildAnalyticsFeature.Voice, token);
        var activity = await ReadVoiceActivityAsync(guildId, data.Period, token);
        var rows = data.Rows; var previous = data.PreviousRows; var sessions = activity.Current.Count; var previousSessions = activity.Previous.Count;
        var seconds = (decimal)activity.Current.Sum(x => x.EligibleSeconds); var previousSeconds = (decimal)activity.Previous.Sum(x => x.EligibleSeconds);
        var participants = await ActiveRecipients(guildId, data.Period.From, data.Period.To, token, "voice");
        var previousParticipants = await ActiveRecipients(guildId, data.Period.PreviousFrom, data.Period.PreviousTo, token, "voice");
        var voiceXp = activity.Current.Sum(x => x.AwardedXp); var previousVoiceXp = activity.Previous.Sum(x => x.AwardedXp);
        var breakdown = activity.Current.GroupBy(x => x.ChannelId).OrderByDescending(x => x.Sum(y => y.EligibleSeconds)).Take(12).Select(x => Breakdown("channel:" + x.Key, x.Sum(y => (decimal)y.EligibleSeconds), activity.Previous.Where(y => y.ChannelId == x.Key).Sum(y => (decimal)y.EligibleSeconds))).Concat(rows.Where(x => x.Outcome is GuildAnalyticsOutcome.Rejected or GuildAnalyticsOutcome.Skipped).GroupBy(x => x.Reason).Take(12).Select(x => Breakdown("nonqualified:" + (x.Key.Length == 0 ? "unspecified" : x.Key), x.Sum(y => y.Count), previous.Where(y => y.Reason == x.Key).Sum(y => y.Count)))).Concat(rows.Where(x => x.Operation.Contains("hub", StringComparison.Ordinal)).GroupBy(x => x.Operation).Take(8).Select(x => Breakdown("hub:" + x.Key, x.Sum(y => y.Count), previous.Where(y => y.Operation == x.Key).Sum(y => y.Count)))).ToArray();
        return new(data.Now, data.Period, [Kpi("qualifiedSeconds", seconds, previousSeconds), Kpi("sessions", sessions, previousSessions), Kpi("averageSessionSeconds", sessions == 0 ? 0 : seconds / sessions, previousSessions == 0 ? 0 : previousSeconds / previousSessions), Kpi("participants", participants, previousParticipants), Kpi("voiceXp", voiceXp, previousVoiceXp), Kpi("hubEvents", rows.Where(x => x.Operation.Contains("hub", StringComparison.Ordinal)).Sum(x => x.Count), previous.Where(x => x.Operation.Contains("hub", StringComparison.Ordinal)).Sum(x => x.Count)), Kpi("transfers", rows.Where(x => x.Operation.Contains("transfer", StringComparison.Ordinal)).Sum(x => x.Count), previous.Where(x => x.Operation.Contains("transfer", StringComparison.Ordinal)).Sum(x => x.Count))], data.Trend, breakdown, data.Insights);
    });

    public Task<GuildAnalyticsFeaturesResponse> FeaturesAsync(ulong guildId, AnalyticsRange range, CancellationToken token) => CachedAsync<GuildAnalyticsFeaturesResponse>($"analytics:features:{guildId}:{range}", async () =>
    {
        var data = await ReadBucketsAsync(guildId, range, null, token); var rows = data.Rows; var previous = data.PreviousRows;
        var successes = rows.Where(x => x.Outcome == GuildAnalyticsOutcome.Succeeded).Sum(x => x.Count); var attempts = rows.Sum(x => x.Count);
        var breakdown = rows.GroupBy(x => new { x.Feature, x.Operation }).OrderByDescending(x => x.Sum(y => y.Count)).Take(30).Select(x => new AnalyticsBreakdown($"module:{x.Key.Feature.ToString().ToLowerInvariant()}:{(x.Key.Operation.Length == 0 ? "unspecified" : x.Key.Operation)}", x.Max(y => y.LastAt).ToString("O"), x.Sum(y => y.Count), previous.Where(y => y.Feature == x.Key.Feature && y.Operation == x.Key.Operation).Sum(y => y.Count), Change(x.Sum(y => y.Count), previous.Where(y => y.Feature == x.Key.Feature && y.Operation == x.Key.Operation).Sum(y => y.Count)))).ToArray();
        return new(data.Now, data.Period, [Kpi("moduleUsage", attempts, previous.Sum(x => x.Count)), Kpi("successful", successes, previous.Where(x => x.Outcome == GuildAnalyticsOutcome.Succeeded).Sum(x => x.Count)), Kpi("successRate", attempts == 0 ? 100 : decimal.Round(successes * 100m / attempts, 2), null), Kpi("routes", rows.Where(x => x.Feature == GuildAnalyticsFeature.Commands).Select(x => x.Operation).Distinct().Count(), previous.Where(x => x.Feature == GuildAnalyticsFeature.Commands).Select(x => x.Operation).Distinct().Count())], data.Trend, breakdown, data.Insights);
    });

    public Task<GuildAnalyticsXpResponse> XpAsync(ulong guildId, AnalyticsRange range, CancellationToken token) => CachedAsync<GuildAnalyticsXpResponse>($"analytics:xp:{guildId}:{range}", async () =>
    {
        var now = timeProvider.GetUtcNow(); var period = CreatePeriod(range, now);
        var entriesTask = database.XpLedger.Aggregate().Match(x => x.GuildId == guildId && x.OccurredAtUtc >= period.PreviousFrom.UtcDateTime && x.OccurredAtUtc <= period.To.UtcDateTime && !x.CooldownDenied && !x.IsProjectionControl)
            .Group(x => new { Day = x.OccurredAtUtc.Date, x.Source, x.UserId }, group => new XpAggregate(group.Key.Day, group.Key.Source, group.Key.UserId, group.Sum(x => x.Amount), group.LongCount())).ToListAsync(token);
        var voiceDaysTask = database.VoiceActivities.Find(x => x.GuildId == guildId && x.DayStartUtc >= period.PreviousFrom.UtcDateTime.Date && x.DayStartUtc <= period.To.UtcDateTime.Date).ToListAsync(token);
        var migrationTask = database.VoiceLedgerMigrationStates.Find(x => x.Id == VoiceLedgerMigrationState.SingletonId).FirstOrDefaultAsync(token);
        await Task.WhenAll(entriesTask, voiceDaysTask, migrationTask);
        var compressedVoiceAuthoritative = VoiceLedgerMigrationService.IsCompressedVoiceAuthoritative(migrationTask.Result);
        var entries = entriesTask.Result.Where(x => x.Source != "voice" || !compressedVoiceAuthoritative).ToList();
        entries.AddRange(VoiceActivityReadModel.Select(voiceDaysTask.Result, compressedVoiceAuthoritative, period.PreviousFrom.UtcDateTime, period.To.UtcDateTime)
            .GroupBy(x => new { Day = x.OccurredAtUtc.Date, x.UserId }).Select(x => new XpAggregate(x.Key.Day, "voice", x.Key.UserId, x.Sum(y => y.AwardedXp), x.LongCount())));
        var current = entries.Where(x => x.Day >= period.From.UtcDateTime).ToArray(); var previous = entries.Where(x => x.Day < period.From.UtcDateTime).ToArray();
        var value = current.Sum(x => x.Value); var old = previous.Sum(x => x.Value);
        var trend = BuildTrend(period, current.GroupBy(x => x.Day).ToDictionary(x => x.Key, x => x.Sum(y => y.Value)));
        var breakdown = current.GroupBy(x => x.Source).OrderByDescending(x => x.Sum(y => y.Value)).Take(12).Select(x => Breakdown(x.Key, x.Sum(y => y.Value), previous.Where(y => y.Source == x.Key).Sum(y => y.Value))).ToArray();
        var recipients = current.Select(x => x.UserId).Distinct().LongCount(); var previousRecipients = previous.Select(x => x.UserId).Distinct().LongCount();
        var levelUps = await LevelUps(guildId, period.From, period.To, token); var oldLevelUps = await LevelUps(guildId, period.PreviousFrom, period.PreviousTo, token);
        var activeSeason = await database.GuildSeasons.Find(x => x.GuildId == guildId && x.Status == SeasonStatus.Active).Project(x => new { x.Name, x.Sequence, x.Number }).FirstOrDefaultAsync(token);
        if (activeSeason != null) breakdown = breakdown.Append(new("activeSeason", activeSeason.Name, activeSeason.Number ?? activeSeason.Sequence, null, null)).ToArray();
        return new(now, period, [Kpi("xpAwarded", value, old), Kpi("activeRecipients", recipients, previousRecipients), Kpi("xpPerActive", recipients == 0 ? 0 : value / recipients, previousRecipients == 0 ? 0 : old / previousRecipients), Kpi("levelUps", levelUps, oldLevelUps), Kpi("roleAwards", 0, null), Kpi("announcements", levelUps, oldLevelUps)], trend, breakdown, []);
    });

    public async Task<GuildAnalyticsAuditResponse> AuditAsync(ulong guildId, AnalyticsAuditQuery query, CancellationToken token)
    {
        var now = timeProvider.GetUtcNow();
        AnalyticsPeriod period;
        if (query.From is not null || query.To is not null)
        {
            if (query.From is not { } customFrom || query.To is not { } customTo || customTo <= customFrom || customTo - customFrom > TimeSpan.FromDays(180) || customTo > now.AddMinutes(1)) throw new ArgumentException("Invalid audit period.");
            period = new("custom", customFrom.ToUniversalTime(), customTo.ToUniversalTime(), customFrom - (customTo - customFrom), customFrom);
        }
        else { if (!TryParseRange(query.Range, out var range)) throw new ArgumentException("Invalid range."); period = CreatePeriod(range, now); }
        if (query.Limit is < 1 or > 100) throw new ArgumentException("Invalid limit.");
        var binding = $"audit|{guildId}|{period.From:O}|{period.To:O}|{NormalizeBinding(query.Type)}|{NormalizeBinding(query.Feature)}|{NormalizeBinding(query.Outcome)}|{query.ActorUserId}|{query.ChannelId}|{NormalizeBinding(query.Search)}|{query.Limit}";
        var filter = Builders<GuildAuditEvent>.Filter.Eq(x => x.GuildId, guildId) & Builders<GuildAuditEvent>.Filter.Ne(x => x.Feature, ExperienceFeature) & Builders<GuildAuditEvent>.Filter.Gte(x => x.OccurredAtUtc, period.From.UtcDateTime) & Builders<GuildAuditEvent>.Filter.Lte(x => x.OccurredAtUtc, period.To.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(query.Type)) filter &= Builders<GuildAuditEvent>.Filter.Eq(x => x.Type, NormalizeFilter(query.Type));
        if (!string.IsNullOrWhiteSpace(query.Feature)) filter &= Builders<GuildAuditEvent>.Filter.Eq(x => x.Feature, NormalizeFilter(query.Feature));
        if (!string.IsNullOrWhiteSpace(query.Outcome)) filter &= Builders<GuildAuditEvent>.Filter.Eq(x => x.Outcome, NormalizeFilter(query.Outcome));
        if (query.ActorUserId is { } actor) filter &= Builders<GuildAuditEvent>.Filter.Eq(x => x.ActorUserId, actor);
        if (query.ChannelId is { } channel) filter &= Builders<GuildAuditEvent>.Filter.Eq(x => x.ChannelId, channel);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            if (query.Search.Length > 100) throw new ArgumentException("Search too long.");
            var regex = new MongoDB.Bson.BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(query.Search.Trim()), "i");
            filter &= Builders<GuildAuditEvent>.Filter.Or(Builders<GuildAuditEvent>.Filter.Regex(x => x.Type, regex), Builders<GuildAuditEvent>.Filter.Regex(x => x.Feature, regex), Builders<GuildAuditEvent>.Filter.Regex(x => x.Action, regex), Builders<GuildAuditEvent>.Filter.Regex(x => x.Outcome, regex), Builders<GuildAuditEvent>.Filter.Regex(x => x.SubjectId, regex), Builders<GuildAuditEvent>.Filter.Regex(x => x.CorrelationId, regex));
        }
        if (query.Cursor is not null)
        {
            if (!cursors.TryRead(query.Cursor, binding, out var cursor)) throw new ArgumentException("Invalid cursor.");
            if (!MongoDB.Bson.ObjectId.TryParse(cursor.Id, out var cursorId)) throw new ArgumentException("Invalid cursor.");
            filter &= Builders<GuildAuditEvent>.Filter.Lt(x => x.OccurredAtUtc, cursor.Timestamp.UtcDateTime) | (Builders<GuildAuditEvent>.Filter.Eq(x => x.OccurredAtUtc, cursor.Timestamp.UtcDateTime) & Builders<GuildAuditEvent>.Filter.Lt("_id", cursorId));
        }
        var documents = await database.GuildAuditEvents.Find(filter).Sort(Builders<GuildAuditEvent>.Sort.Descending(x => x.OccurredAtUtc).Descending("_id")).Limit(query.Limit + 1).ToListAsync(token);
        var page = documents.Take(query.Limit).ToArray(); var last = page.LastOrDefault();
        var next = documents.Count > query.Limit && last?.Id != null ? cursors.Create(new DateTimeOffset(DateTime.SpecifyKind(last.OccurredAtUtc, DateTimeKind.Utc)), last.Id, binding) : null;
        var hubIds = page.SelectMany(x => x.Metadata.TryGetValue("hubId", out var hubId) && !string.IsNullOrWhiteSpace(hubId) ? [hubId] : Array.Empty<string>()).Distinct().ToArray();
        var hubNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (hubIds.Length > 0)
        {
            var hubs = await database.VcHubs.Find(x => x.GuildId == guildId && x.Id != null && hubIds.Contains(x.Id)).Project(x => new { x.Id, x.HubChannelName }).ToListAsync(token);
            foreach (var hub in hubs) if (hub.Id != null && !string.IsNullOrWhiteSpace(hub.HubChannelName)) hubNames[hub.Id] = hub.HubChannelName;
        }
        GuildDiscordContext? context = null;
        try { context = await discord.ResolveAsync(guildId, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { /* Presentation lookup must not make the audit log unavailable. */ }
        var items = page.Select(x =>
        {
            var actorName = x.ActorUserId is { } actorId ? context?.Guild.GetUser(actorId)?.DisplayName : null;
            var subjectName = ulong.TryParse(x.SubjectId, out var subjectId) ? context?.Guild.GetUser(subjectId)?.DisplayName : null;
            var channelName = x.ChannelId is { } channelId ? context?.Guild.GetChannel(channelId)?.Name : null;
            var hubName = x.Metadata.TryGetValue("hubId", out var hubId) ? hubNames.GetValueOrDefault(hubId) : null;
            return new AnalyticsAuditItem(x.Id!, Utc(x.OccurredAtUtc), $"{x.Feature}.{x.Action}", x.ActorUserId?.ToString(), actorName, x.SubjectId, subjectName, x.Outcome, EmptyToNull(x.CorrelationId), x.Metadata, x.ChannelId?.ToString(), channelName, hubName);
        }).ToArray();
        return new(now, period, [Kpi("events", items.Length, null)], [], [], [], items, next);
    }

    private async Task<BucketData> ReadBucketsAsync(ulong guildId, AnalyticsRange range, GuildAnalyticsFeature? feature, CancellationToken token)
    {
        var now = timeProvider.GetUtcNow(); var period = CreatePeriod(range, now); var granularity = range == AnalyticsRange.Last24Hours ? GuildAnalyticsGranularity.Hour : GuildAnalyticsGranularity.Day;
        var filter = Builders<GuildAnalyticsBucket>.Filter.Eq(x => x.GuildId, guildId) & Builders<GuildAnalyticsBucket>.Filter.Eq(x => x.Granularity, granularity) & Builders<GuildAnalyticsBucket>.Filter.Gte(x => x.BucketStartUtc, period.PreviousFrom.UtcDateTime) & Builders<GuildAnalyticsBucket>.Filter.Lte(x => x.BucketStartUtc, period.To.UtcDateTime);
        if (feature is { } selected) filter &= Builders<GuildAnalyticsBucket>.Filter.Eq(x => x.Feature, selected);
        var rows = await database.GuildAnalyticsBuckets.Aggregate().Match(filter).Group(x => new { x.BucketStartUtc, x.Metric, x.Feature, x.Outcome, x.Operation, x.Source, x.Reason, x.ChannelId }, x => new BucketAggregate(x.Key.BucketStartUtc, x.Key.Metric, x.Key.Feature, x.Key.Outcome, x.Key.Operation, x.Key.Source, x.Key.Reason, x.Key.ChannelId, x.Sum(y => y.Count), x.Sum(y => y.Value), x.Sum(y => y.DurationSeconds), x.Max(y => y.UpdatedAtUtc))).Limit(2_000).ToListAsync(token); var current = rows.Where(x => x.BucketStartUtc >= period.From.UtcDateTime).ToArray(); var previous = rows.Where(x => x.BucketStartUtc < period.From.UtcDateTime).ToArray();
        var kpis = new[] { Kpi("events", current.Sum(x => x.Count), previous.Sum(x => x.Count)), Kpi("value", current.Sum(x => x.Value), previous.Sum(x => x.Value)), Kpi("durationSeconds", (decimal)current.Sum(x => x.DurationSeconds), (decimal)previous.Sum(x => x.DurationSeconds)), Kpi("failures", current.Where(x => x.Outcome == GuildAnalyticsOutcome.Failed).Sum(x => x.Count), previous.Where(x => x.Outcome == GuildAnalyticsOutcome.Failed).Sum(x => x.Count)) };
        var trend = BuildTrend(period, current.GroupBy(x => x.BucketStartUtc).ToDictionary(x => x.Key, x => (decimal)x.Sum(y => y.Count)));
        var breakdown = current.GroupBy(x => x.Feature).OrderByDescending(x => x.Sum(y => y.Count)).Select(x => Breakdown(x.Key.ToString(), x.Sum(y => y.Count), previous.Where(y => y.Feature == x.Key).Sum(y => y.Count))).ToArray();
        var insights = current.Any(x => x.Outcome == GuildAnalyticsOutcome.Failed) ? new[] { new AnalyticsInsight("failuresDetected", "warning", current.Where(x => x.Outcome == GuildAnalyticsOutcome.Failed).Sum(x => x.Count), new Dictionary<string, object?>()) } : [];
        return new(now, period, kpis, trend, breakdown, insights.Take(3).ToArray(), current, previous);
    }

    private async Task<T> CachedAsync<T>(string key, Func<Task<T>> factory) where T : class
    {
        if (cache.TryGetValue(key, out T? result) && result != null) return result;
        result = await factory(); cache.Set(key, result, TimeSpan.FromSeconds(20)); return result;
    }
    private static IReadOnlyList<AnalyticsTrendPoint> BuildTrend(AnalyticsPeriod period, IReadOnlyDictionary<DateTime, decimal> values)
    {
        var step = period.Range == "24h" ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1); var points = new List<AnalyticsTrendPoint>();
        for (var at = period.From; at < period.To; at += step) { values.TryGetValue(at.UtcDateTime, out var value); values.TryGetValue((at - (period.To - period.From)).UtcDateTime, out var old); points.Add(new(at, value, old)); }
        return points;
    }
    private static AnalyticsKpi Kpi(string code, decimal value, decimal? previous) => new(code, value, previous, Change(value, previous), previous is null || value == previous ? "flat" : value > previous ? "up" : "down");
    private static AnalyticsBreakdown Breakdown(string code, decimal value, decimal previous) => new(code, null, value, previous, Change(value, previous));
    private static decimal? Change(decimal value, decimal? previous) => previous is null or 0 ? null : decimal.Round((value - previous.Value) * 100m / Math.Abs(previous.Value), 2);
    private static string Key(AnalyticsRange range) => range switch { AnalyticsRange.Last24Hours => "24h", AnalyticsRange.Last7Days => "7d", AnalyticsRange.Last30Days => "30d", _ => "90d" };
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;
    private static string NormalizeBinding(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
    private static string NormalizeFilter(string value) { var normalized = value.Trim().ToLowerInvariant(); if (normalized.Length is 0 or > 80 || normalized.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('.' or '_' or '-'))) throw new ArgumentException("Invalid filter."); return normalized; }
    private async Task<long> ActiveRecipients(ulong guildId, DateTimeOffset from, DateTimeOffset to, CancellationToken token, string? source = null)
    {
        var filter = Builders<XpLedgerEntry>.Filter.Eq(x => x.GuildId, guildId) & Builders<XpLedgerEntry>.Filter.Gte(x => x.OccurredAtUtc, from.UtcDateTime) & Builders<XpLedgerEntry>.Filter.Lt(x => x.OccurredAtUtc, to.UtcDateTime) & Builders<XpLedgerEntry>.Filter.Ne(x => x.CooldownDenied, true) & Builders<XpLedgerEntry>.Filter.Ne(x => x.IsProjectionControl, true);
        if (source != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.Source, source);
        var ledgerTask = database.XpLedger.Aggregate().Match(filter).Group(x => x.UserId, x => new ParticipantAggregate(x.Key)).ToListAsync(token);
        if (source != null && source != "voice") return (await ledgerTask).LongCount();
        var migrationTask = database.VoiceLedgerMigrationStates.Find(x => x.Id == VoiceLedgerMigrationState.SingletonId).FirstOrDefaultAsync(token);
        var daysTask = database.VoiceActivities.Find(x => x.GuildId == guildId && x.DayStartUtc >= from.UtcDateTime.Date && x.DayStartUtc <= to.UtcDateTime.Date).ToListAsync(token);
        await Task.WhenAll(ledgerTask, migrationTask, daysTask);
        var compressedVoiceAuthoritative = VoiceLedgerMigrationService.IsCompressedVoiceAuthoritative(migrationTask.Result);
        var recipients = ledgerTask.Result.Select(x => x.UserId).ToHashSet();
        if (compressedVoiceAuthoritative)
        {
            if (source == "voice") recipients.Clear();
            else recipients = (await database.XpLedger.Distinct(x => x.UserId, filter & Builders<XpLedgerEntry>.Filter.Ne(x => x.Source, "voice"), cancellationToken: token).ToListAsync(token)).ToHashSet();
        }
        recipients.UnionWith(VoiceActivityReadModel.Select(daysTask.Result, compressedVoiceAuthoritative, from.UtcDateTime, to.UtcDateTime).Select(x => x.UserId));
        return recipients.Count;
    }

    private async Task<VoicePeriodData> ReadVoiceActivityAsync(ulong guildId, AnalyticsPeriod period, CancellationToken token)
    {
        var daysTask = database.VoiceActivities.Find(x => x.GuildId == guildId && x.DayStartUtc >= period.PreviousFrom.UtcDateTime.Date && x.DayStartUtc <= period.To.UtcDateTime.Date).ToListAsync(token);
        var migrationTask = database.VoiceLedgerMigrationStates.Find(x => x.Id == VoiceLedgerMigrationState.SingletonId).FirstOrDefaultAsync(token);
        var ledgerTask = database.XpLedger.Find(x => x.GuildId == guildId && x.Source == "voice" && x.OccurredAtUtc >= period.PreviousFrom.UtcDateTime && x.OccurredAtUtc < period.To.UtcDateTime && !x.CooldownDenied && !x.IsProjectionControl).ToListAsync(token);
        await Task.WhenAll(daysTask, migrationTask, ledgerTask);
        var authoritative = VoiceLedgerMigrationService.IsCompressedVoiceAuthoritative(migrationTask.Result);
        var items = VoiceActivityReadModel.Select(daysTask.Result, authoritative, period.PreviousFrom.UtcDateTime, period.To.UtcDateTime).ToList();
        if (!authoritative) items.AddRange(ledgerTask.Result.Select(x => new VoiceActivityReadItem(x.UserId, x.OccurredAtUtc, x.Amount,
            x.PeriodStartsAtUtc != null && x.PeriodEndsAtUtc > x.PeriodStartsAtUtc ? (long)(x.PeriodEndsAtUtc.Value - x.PeriodStartsAtUtc.Value).TotalSeconds : 0, x.ChannelId ?? 0)));
        return new(items.Where(x => x.OccurredAtUtc >= period.From.UtcDateTime).ToArray(), items.Where(x => x.OccurredAtUtc < period.From.UtcDateTime).ToArray());
    }
    private async Task<long> LevelUps(ulong guildId, DateTimeOffset from, DateTimeOffset to, CancellationToken token) => await database.LevelTransitionEvents.CountDocumentsAsync(x => x.GuildId == guildId && x.CreatedAtUtc >= from.UtcDateTime && x.CreatedAtUtc < to.UtcDateTime && x.NewLevel > x.PreviousLevel, cancellationToken: token);
    internal static TimelinePeriod CreateTimelinePeriod(AnalyticsTimelineQuery query, DateTimeOffset now)
    {
        var utcNow = now.ToUniversalTime();
        DateTimeOffset start;
        DateTimeOffset end;
        string range;
        if (query.From is not null || query.To is not null)
        {
            if (query.From is not { } from || query.To is not { } to || query.Range is not (null or "" or "custom")) throw new ArgumentException("Invalid custom range.");
            start = from.ToUniversalTime();
            end = to.ToUniversalTime();
            if (end <= start || end - start > TimeSpan.FromDays(180) || end > utcNow.AddMinutes(1)) throw new ArgumentException("Invalid custom range.");
            range = "custom";
        }
        else
        {
            var days = query.Range switch { "24h" => 1, null or "" or "7d" => 7, "30d" => 30, "90d" => 90, _ => throw new ArgumentException("Invalid range.") };
            start = query.Range == "24h" ? utcNow.AddDays(-1) : new DateTimeOffset(utcNow.UtcDateTime.Date.AddDays(-(days - 1)), TimeSpan.Zero);
            end = utcNow;
            range = query.Range is null or "" ? "7d" : query.Range;
        }

        var bucket = query.Bucket switch
        {
            null or "" or "auto" => end - start > TimeSpan.FromDays(30) ? TimelineBucketSize.Week : TimelineBucketSize.Day,
            "day" => TimelineBucketSize.Day,
            "week" => TimelineBucketSize.Week,
            _ => throw new ArgumentException("Invalid bucket size.")
        };
        return new(range, start, end, bucket);
    }

    internal static GuildAnalyticsTimelineResponse BuildTimeline(TimelinePeriod period, DateTimeOffset generatedAt, IReadOnlyList<TimelineActivityRow> rows, IReadOnlyList<DateTimeOffset> levelUps)
    {
        var comparisonOffset = TimelineComparisonOffset(period);
        var previousStart = period.Start - comparisonOffset;
        var current = Aggregate(rows, levelUps, period.Start, period.End);
        var previous = Aggregate(rows, levelUps, previousStart, period.Start);
        var step = period.BucketSize == TimelineBucketSize.Day ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
        var buckets = new List<AnalyticsTimelineBucket>();
        var previousBuckets = new List<AnalyticsTimelineBucket>();
        for (var start = period.Start; start < period.End; start += step)
        {
            var nominalEnd = start + step;
            var end = nominalEnd < period.End ? nominalEnd : period.End;
            var values = Aggregate(rows, levelUps, start, end);
            buckets.Add(new(start, end, end - start < step || end >= generatedAt && start < generatedAt, values.ActiveMembers, values.ActiveVoiceMembers, values.QualifiedVoiceSeconds, values.AwardedXp, values.Activities, values.LevelUps));
            var previousBucketStart = start - comparisonOffset;
            var previousBucketEnd = end - comparisonOffset;
            var previousValues = Aggregate(rows, levelUps, previousBucketStart, previousBucketEnd);
            previousBuckets.Add(new(previousBucketStart, previousBucketEnd, false, previousValues.ActiveMembers, previousValues.ActiveVoiceMembers, previousValues.QualifiedVoiceSeconds, previousValues.AwardedXp, previousValues.Activities, previousValues.LevelUps));
        }
        return new(period.Start, period.End, "UTC", period.BucketSize == TimelineBucketSize.Day ? "day" : "week", generatedAt, new(current, previous), buckets, previousBuckets);
    }

    private static AnalyticsTimelineAggregate Aggregate(IReadOnlyList<TimelineActivityRow> rows, IReadOnlyList<DateTimeOffset> levelUps, DateTimeOffset start, DateTimeOffset end)
    {
        var members = new HashSet<ulong>();
        var voiceMembers = new HashSet<ulong>();
        decimal voiceXp = 0, messageXp = 0, reactionXp = 0, otherXp = 0;
        long voiceActivities = 0, messageActivities = 0, reactionActivities = 0, otherActivities = 0, voiceSeconds = 0;
        foreach (var row in rows.Where(x => x.OccurredAt >= start && x.OccurredAt < end))
        {
            members.Add(row.UserId);
            switch (TimelineSource(row.Source))
            {
                case "voice": voiceMembers.Add(row.UserId); voiceXp += row.AwardedXp; voiceActivities = checked(voiceActivities + row.ActivityCount); voiceSeconds = checked(voiceSeconds + row.QualifiedVoiceSeconds); break;
                case "messages": messageXp += row.AwardedXp; messageActivities = checked(messageActivities + row.ActivityCount); break;
                case "reactions": reactionXp += row.AwardedXp; reactionActivities = checked(reactionActivities + row.ActivityCount); break;
                default: otherXp += row.AwardedXp; otherActivities = checked(otherActivities + row.ActivityCount); break;
            }
        }
        var totalXp = voiceXp + messageXp + reactionXp + otherXp;
        var totalActivities = checked(checked(voiceActivities + messageActivities) + checked(reactionActivities + otherActivities));
        var levelUpCount = levelUps.LongCount(x => x >= start && x < end);
        return new(members.Count, voiceMembers.Count, voiceSeconds, new(totalXp, voiceXp, messageXp, reactionXp, otherXp), new(totalActivities, voiceActivities, messageActivities, reactionActivities, otherActivities), levelUpCount);
    }

    private static long VoiceSeconds(TimelineLedgerDocument row) => row.Source == "voice" && row.PeriodStartsAtUtc != null && row.PeriodEndsAtUtc > row.PeriodStartsAtUtc
        ? checked((long)(row.PeriodEndsAtUtc.Value - row.PeriodStartsAtUtc.Value).TotalSeconds)
        : 0;
    private static string TimelineSource(string source) => source switch { "voice" => "voice", "message" => "messages", "reaction" => "reactions", _ => "other" };
    private static TimeSpan TimelineComparisonOffset(TimelinePeriod period) => period.Range switch { "24h" => TimeSpan.FromDays(1), "7d" => TimeSpan.FromDays(7), "30d" => TimeSpan.FromDays(30), "90d" => TimeSpan.FromDays(90), _ => period.End - period.Start };
    internal enum TimelineBucketSize { Day, Week }
    internal sealed record TimelinePeriod(string Range, DateTimeOffset Start, DateTimeOffset End, TimelineBucketSize BucketSize);
    internal sealed record TimelineActivityRow(ulong UserId, DateTimeOffset OccurredAt, string Source, decimal AwardedXp, long QualifiedVoiceSeconds, long ActivityCount);
    private sealed record TimelineLedgerDocument(ulong UserId, DateTime OccurredAtUtc, string Source, decimal Amount, DateTime? PeriodStartsAtUtc, DateTime? PeriodEndsAtUtc);
    private sealed record ParticipantAggregate(ulong UserId);
    private sealed record XpAggregate(DateTime Day, string Source, ulong UserId, decimal Value, long Count);
    private sealed record BucketAggregate(DateTime BucketStartUtc, GuildAnalyticsMetric Metric, GuildAnalyticsFeature Feature, GuildAnalyticsOutcome Outcome, string Operation, string Source, string Reason, ulong? ChannelId, long Count, long Value, double DurationSeconds, DateTime LastAt);
    private sealed record BucketData(DateTimeOffset Now, AnalyticsPeriod Period, IReadOnlyList<AnalyticsKpi> Kpis, IReadOnlyList<AnalyticsTrendPoint> Trend, IReadOnlyList<AnalyticsBreakdown> Breakdown, IReadOnlyList<AnalyticsInsight> Insights, IReadOnlyList<BucketAggregate> Rows, IReadOnlyList<BucketAggregate> PreviousRows);
    private sealed record VoicePeriodData(IReadOnlyList<VoiceActivityReadItem> Current, IReadOnlyList<VoiceActivityReadItem> Previous);
}
