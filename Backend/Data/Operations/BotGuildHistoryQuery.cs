using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Data.Analytics;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Operations;

public sealed record BotGuildHistoryItem(string Id, string BotId, string GuildId, string GuildName, string Identity, int MemberCount, DateTimeOffset JoinedAt, DateTimeOffset FirstObservedAt, bool JoinObserved, DateTimeOffset? RemovedAt, string? RemovalEvidence);
public sealed record BotGuildHistoryResponse(DateTimeOffset GeneratedAt, OperationsRange Range, IReadOnlyList<OperationsMetric> Metrics, IReadOnlyList<BotGuildHistoryItem> Items, long Total, int? NextOffset, DateTimeOffset? TrackingSince);

public sealed class BotGuildHistoryQuery(RankoonDbContext database, TimeProvider timeProvider, BotGuildHistoryRecorder recorder)
{
    public async Task<BotGuildHistoryResponse> QueryAsync(AnalyticsRange range, string? search, string? status, int offset, CancellationToken token)
    {
        search = search?.Trim();
        if (offset is < 0 or > 1_000_000 || search?.Length > 100 || status is not (null or "" or "all" or "present" or "removed")) throw new ArgumentException("Invalid history query.");
        var now = timeProvider.GetUtcNow();
        var period = GuildAnalyticsQueryService.CreatePeriod(range, now);
        var f = Builders<BotGuildInstallation>.Filter;
        var from = period.From.UtcDateTime; var to = period.To.UtcDateTime;
        var installs = await database.BotGuildInstallations.CountDocumentsAsync(x => x.JoinObserved && x.JoinedAtUtc >= from && x.JoinedAtUtc <= to, cancellationToken: token);
        var removals = await database.BotGuildInstallations.CountDocumentsAsync(x => x.RemovalEvidence == "observed" && x.RemovedAtUtc >= from && x.RemovedAtUtc <= to, cancellationToken: token);
        var detected = await database.BotGuildInstallations.CountDocumentsAsync(x => x.RemovalEvidence == "detected" && x.RemovedAtUtc >= from && x.RemovedAtUtc <= to, cancellationToken: token);
        var baseline = await database.BotGuildInstallations.CountDocumentsAsync(x => !x.JoinObserved && x.FirstObservedAtUtc >= from && x.FirstObservedAtUtc <= to, cancellationToken: token);
        var first = await database.BotGuildInstallations.Find(f.Empty).SortBy(x => x.FirstObservedAtUtc).FirstOrDefaultAsync(token);
        var filter = f.Where(x => (x.FirstObservedAtUtc >= from && x.FirstObservedAtUtc <= to) || (x.RemovedAtUtc >= from && x.RemovedAtUtc <= to));
        if (status == "present") filter &= f.Eq(x => x.RemovedAtUtc, null);
        if (status == "removed") filter &= f.Ne(x => x.RemovedAtUtc, null);
        if (!string.IsNullOrEmpty(search))
        {
            var name = f.Regex(x => x.GuildName, new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(search), "i"));
            filter &= ulong.TryParse(search, out var id) ? name | f.Eq(x => x.GuildId, id) | f.Eq(x => x.BotId, id) : name;
        }
        var total = await database.BotGuildInstallations.CountDocumentsAsync(filter, cancellationToken: token);
        var rows = await database.BotGuildInstallations.Find(filter).SortByDescending(x => x.HistoryAtUtc).ThenByDescending(x => x.Id).Skip(offset).Limit(50).ToListAsync(token);
        static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        static OperationsMetric Metric(string code, decimal value) => new(code, value, null, null);
        var metrics = new List<OperationsMetric> { Metric("botInstallations", installs), Metric("botRemovals", removals), Metric("detectedRemovals", detected), Metric("baselineInstallations", baseline) };
        if (recorder.PersistenceFailureCount > 0) metrics.Add(Metric("historyWriteFailures", recorder.PersistenceFailureCount));
        return new(now, new(period.Range, period.From, period.To), metrics,
            rows.Select(x => new BotGuildHistoryItem(x.Id, x.BotId.ToString(), x.GuildId.ToString(), x.GuildName, x.Identity, x.MemberCount, Utc(x.JoinedAtUtc), Utc(x.FirstObservedAtUtc), x.JoinObserved, x.RemovedAtUtc is { } removed ? Utc(removed) : null, x.RemovalEvidence)).ToArray(),
            total, offset + rows.Count < total ? offset + rows.Count : null, first == null ? null : Utc(first.FirstObservedAtUtc));
    }
}
