using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Rankoon.Data.Analytics;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Operations;
using Xunit;

namespace Rankoon.Backend.Tests;

public sealed class BotGuildHistoryTests : IAsyncLifetime
{
    private readonly string name = $"rankoon_history_test_{Guid.NewGuid():N}";
    private MongoClient client = null!;
    private RankoonDbContext database = null!;
    private BotGuildHistoryRecorder recorder = null!;

    public Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("RANKOON_SEASON_TEST_MONGO");
        if (connection == null) return Task.CompletedTask;
        client = new(connection);
        database = new(Options.Create(new MongoDbSettings { ConnectionString = connection, DatabaseName = name }));
        recorder = new(database, TimeProvider.System, NullLogger<BotGuildHistoryRecorder>.Instance);
        return Task.CompletedTask;
    }
    public Task DisposeAsync() => client == null ? Task.CompletedTask : client.DropDatabaseAsync(name);

    [SeasonMongoFact]
    public async Task DuplicateNotificationsAndReconnectsPreserveOneInstallationAndFirstRemoval()
    {
        var joined = DateTimeOffset.UtcNow.AddHours(-1);
        await recorder.ObserveMembershipAsync(10, "Rankoon", 20, "Original", 50, joined, false);
        // GuildAvailable can precede JoinedGuild; the later event must upgrade the baseline evidence.
        await recorder.ObserveMembershipAsync(10, "Rankoon", 20, "Original", 50, joined, true);
        await recorder.ObserveMembershipAsync(10, "Rankoon", 20, "Renamed", 51, joined, false);
        var original = await database.BotGuildInstallations.Find(x => true).SingleAsync();
        Assert.True(original.JoinObserved);
        Assert.Equal("Renamed", original.GuildName);
        await recorder.RemovedMembershipAsync(10, 20, joined);
        var removed = await database.BotGuildInstallations.Find(x => true).SingleAsync();
        await recorder.RemovedMembershipAsync(10, 20, joined);
        await recorder.ObserveMembershipAsync(10, "Rankoon", 20, "Renamed", 51, joined, false);
        var repeated = await database.BotGuildInstallations.Find(x => true).SingleAsync();
        Assert.Equal(removed.RemovedAtUtc, repeated.RemovedAtUtc);
        Assert.Equal("observed", repeated.RemovalEvidence);
    }

    [SeasonMongoFact]
    public async Task ReinstallationAndCustomBotHaveSeparateHistoriesAndSafeStaleDeparture()
    {
        var first = DateTimeOffset.UtcNow.AddDays(-2); var second = first.AddDays(1);
        await recorder.ObserveMembershipAsync(10, "Rankoon", 20, "Server", 50, first, true);
        await recorder.RemovedMembershipAsync(10, 20, first);
        await recorder.ObserveMembershipAsync(10, "Rankoon", 20, "Server", 50, second, true);
        await recorder.ObserveMembershipAsync(11, "Custom", 20, "Server", 50, first, false);
        await recorder.RemovedMembershipAsync(10, 20, first);
        var rows = await database.BotGuildInstallations.Find(x => true).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows.Count(x => x.RemovedAtUtc == null));
    }

    [SeasonMongoFact]
    public async Task ReconciliationRetainsUnavailableGuildsAndOtherShardsAndDetectsOfflineRemoval()
    {
        // Snowflakes map to shards by (guildId >> 22) % totalShards.
        const ulong unavailable = 8_388_608, otherShard = 4_194_304, removed = 16_777_216;
        var joined = DateTimeOffset.UtcNow.AddDays(-2);
        foreach (var guild in new[] { unavailable, otherShard, removed })
            await recorder.ObserveMembershipAsync(10, "Rankoon", guild, "Server", 50, joined, false);
        await recorder.ReconcileMissingAsync(10, 2, 0, new Dictionary<ulong, string?> { [unavailable] = null }, DateTime.UtcNow);
        var rows = await database.BotGuildInstallations.Find(x => true).ToListAsync();
        Assert.Null(rows.Single(x => x.GuildId == unavailable).RemovedAtUtc);
        Assert.Null(rows.Single(x => x.GuildId == otherShard).RemovedAtUtc);
        Assert.Equal("detected", rows.Single(x => x.GuildId == removed).RemovalEvidence);
        var query = new BotGuildHistoryQuery(database, TimeProvider.System, recorder);
        var report = await query.QueryAsync(AnalyticsRange.Last7Days, "Server", "all", 0, default);
        Assert.Equal(0, report.Metrics.Single(x => x.Code == "botInstallations").Value);
        Assert.Equal(3, report.Metrics.Single(x => x.Code == "baselineInstallations").Value);
        Assert.Equal(1, report.Metrics.Single(x => x.Code == "detectedRemovals").Value);
        Assert.Equal(0, report.Metrics.Single(x => x.Code == "botRemovals").Value);
    }

    [SeasonMongoFact]
    public async Task StaleSnapshotDoesNotRemoveAConcurrentObservationAndReinstallClosesOldMembership()
    {
        var joined = DateTimeOffset.UtcNow.AddDays(-2);
        var staleSnapshot = DateTime.UtcNow.AddMinutes(-1);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => recorder.ObserveMembershipAsync(10, "Rankoon", 20, "Server", 50, joined, false)));
        await recorder.ReconcileMissingAsync(10, 1, 0, new Dictionary<ulong, string?>(), staleSnapshot);
        Assert.Null((await database.BotGuildInstallations.Find(x => true).SingleAsync()).RemovedAtUtc);
        var newJoin = joined.AddDays(1);
        await recorder.ObserveMembershipAsync(10, "Rankoon", 20, "Server", 50, newJoin, false);
        await recorder.ReconcileMissingAsync(10, 1, 0, new Dictionary<ulong, string?> { [20] = BotGuildHistoryRecorder.InstallationId(10, 20, newJoin) }, DateTime.UtcNow);
        var rows = await database.BotGuildInstallations.Find(x => true).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("detected", rows.Single(x => x.Id == BotGuildHistoryRecorder.InstallationId(10, 20, joined)).RemovalEvidence);
        Assert.Single(rows, x => x.RemovedAtUtc == null);
    }

    [SeasonMongoFact]
    public async Task QueryEscapesSearchAndPaginatesWithoutDuplicatingEntries()
    {
        var joined = DateTimeOffset.UtcNow.AddDays(-1);
        for (ulong i = 1; i <= 55; i++) await recorder.ObserveMembershipAsync(10, "Rankoon", i, "Server [test]", 50, joined, true);
        var query = new BotGuildHistoryQuery(database, TimeProvider.System, recorder);
        var first = await query.QueryAsync(AnalyticsRange.Last7Days, "[test]", "present", 0, default);
        var second = await query.QueryAsync(AnalyticsRange.Last7Days, "[test]", "present", first.NextOffset!.Value, default);
        Assert.Equal(55, first.Total);
        Assert.Equal(50, first.Items.Count);
        Assert.Equal(5, second.Items.Count);
        Assert.Empty(first.Items.Select(x => x.Id).Intersect(second.Items.Select(x => x.Id)));
        Assert.Null(second.NextOffset);
        await Assert.ThrowsAsync<ArgumentException>(() => query.QueryAsync(AnalyticsRange.Last7Days, null, "invalid", 0, default));
    }
}
