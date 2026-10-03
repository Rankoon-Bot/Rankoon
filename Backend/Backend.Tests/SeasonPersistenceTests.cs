using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Operations;
using Rankoon.Data.Reporting;
using Rankoon.Data.Xp;
using Xunit;

namespace Rankoon.Backend.Tests;

// Run against an isolated MongoDB: RANKOON_SEASON_TEST_MONGO=mongodb://127.0.0.1:27028
public sealed class SeasonMongoFactAttribute : FactAttribute
{
    public SeasonMongoFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RANKOON_SEASON_TEST_MONGO")))
            Skip = "Set RANKOON_SEASON_TEST_MONGO to run MongoDB season regression tests.";
    }
}

public sealed class SeasonPersistenceTests : IAsyncLifetime
{
    private readonly string databaseName = $"rankoon_season_test_{Guid.NewGuid():N}";
    private MongoClient client = null!;
    private RankoonDbContext database = null!;
    private SeasonService settings = null!;
    private SeasonPlanningService planning = null!;
    private SeasonLifecycleService lifecycle = null!;
    private readonly Reports reports = new();
    private readonly Realtime realtime = new();

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("RANKOON_SEASON_TEST_MONGO");
        if (connection == null) return;
        client = new MongoClient(connection);
        database = new(Options.Create(new MongoDbSettings { ConnectionString = connection, DatabaseName = databaseName }));
        settings = new(database, TimeProvider.System);
        planning = new(database, settings, TimeProvider.System);
        lifecycle = new(database, reports, realtime, new(database, new Discord(), TimeProvider.System), TimeProvider.System);
        await database.GuildSeasonSettings.Indexes.CreateOneAsync(new CreateIndexModel<GuildSeasonSettings>(Builders<GuildSeasonSettings>.IndexKeys.Ascending(x => x.GuildId), new() { Unique = true }));
        await database.GuildSeasons.Indexes.CreateManyAsync([
            new(Builders<GuildSeason>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.Sequence), new() { Unique = true }),
            new(Builders<GuildSeason>.IndexKeys.Ascending(x => x.ActiveGuildId), new() { Unique = true, Sparse = true })
        ]);
        await database.SeasonPlanningLeases.Indexes.CreateOneAsync(new CreateIndexModel<SeasonPlanningLease>(Builders<SeasonPlanningLease>.IndexKeys.Ascending(x => x.GuildId), new() { Unique = true }));
        await database.SeasonCoordinatorLeases.Indexes.CreateOneAsync(new CreateIndexModel<SeasonCoordinatorLease>(Builders<SeasonCoordinatorLease>.IndexKeys.Ascending(x => x.GuildId), new() { Unique = true }));
        await database.SeasonSetupOperations.Indexes.CreateOneAsync(new CreateIndexModel<SeasonSetupOperation>(Builders<SeasonSetupOperation>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.OperationId), new() { Unique = true }));
        await database.SeasonMemberXp.Indexes.CreateOneAsync(new CreateIndexModel<SeasonMemberXp>(Builders<SeasonMemberXp>.IndexKeys.Ascending(x => x.SeasonId).Ascending(x => x.UserId), new() { Unique = true }));
    }

    public Task DisposeAsync() => client == null ? Task.CompletedTask : client.DropDatabaseAsync(databaseName);

    [SeasonMongoFact]
    public async Task Concurrent_setup_retries_create_exactly_one_plan_and_keep_snapshot()
    {
        var other = new SeasonPlanningService(database, settings, TimeProvider.System);
        var requests = Enumerable.Range(0, 12).Select(index => (index % 2 == 0 ? planning : other).SetupAsync(Config(), 2, "same-request"));
        var results = await Task.WhenAll(requests);
        Assert.All(results, result => Assert.Equal(results[0].Seasons.Select(x => x.Id), result.Seasons.Select(x => x.Id)));
        var all = await settings.GetSeasonsAsync(1);
        Assert.Equal(2, all.Count);
        Assert.All(all, season => Assert.Equal(1, season.SettingsSnapshot.Revision));
        Assert.Equal(1, (await settings.GetSettingsAsync(1)).Revision);
        Assert.True((await database.SeasonSetupOperations.Find(x => x.OperationId == "same-request").SingleAsync()).Completed);
    }

    [SeasonMongoFact]
    public async Task Reusing_operation_id_with_changed_input_is_rejected()
    {
        await planning.SetupAsync(Config(), 1, "request");
        var different = Config(); different.FixedDurationDays = 14;
        await Assert.ThrowsAsync<SeasonPlanningConflictException>(() => planning.SetupAsync(different, 1, "request"));
        Assert.Single(await settings.GetSeasonsAsync(1));
    }

    [SeasonMongoFact]
    public async Task Completed_retry_does_not_recreate_a_deleted_season()
    {
        var result = await planning.SetupAsync(Config(), 1, "request");
        await using (await planning.AcquireLeaseAsync(1, default))
        {
            Assert.True(await lifecycle.CancelAsync(1, result.Seasons[0].Id!));
            Assert.True(await lifecycle.DeleteCancelledAsync(1, result.Seasons[0].Id!));
        }
        var retry = await planning.SetupAsync(Config(), 1, "request");
        Assert.Empty(retry.Seasons);
        Assert.Empty(await settings.GetSeasonsAsync(1));
    }

    [SeasonMongoFact]
    public async Task Coordinator_recovers_interrupted_setup_even_before_settings_were_enabled()
    {
        var snapshot = Config(); snapshot.Revision = 1;
        var anchor = snapshot.ScheduleAnchorUtc!.Value;
        await database.SeasonSetupOperations.InsertOneAsync(new()
        {
            GuildId = 1, OperationId = "interrupted", Settings = snapshot,
            Candidates = [new() { Sequence = 1, Number = 1, Name = "Season 1", StartsAtUtc = anchor, EndsAtUtc = anchor.AddDays(7) }]
        });
        await Coordinator().RunOnceAsync();
        var season = Assert.Single(await settings.GetSeasonsAsync(1));
        Assert.Equal(SeasonStatus.Active, season.Status);
        Assert.True(season.BaselineInitialized);
        Assert.True((await settings.GetSettingsAsync(1)).Enabled);
        Assert.True((await database.SeasonSetupOperations.Find(x => x.OperationId == "interrupted").SingleAsync()).Completed);
    }

    [SeasonMongoFact]
    public async Task Cancelling_stops_buffer_and_coordinator_does_not_replace_cancelled_work()
    {
        var config = Config(); config.PlanningMode = SeasonPlanningMode.MaintainPreparedBuffer; config.PreparedSeasonCount = 2;
        await planning.SetupAsync(config, 2, "request");
        await using (await planning.AcquireLeaseAsync(1, default)) await lifecycle.CancelScheduledAsync(1);
        await Coordinator().RunOnceAsync();
        Assert.Equal(SeasonPlanningMode.Explicit, (await settings.GetSettingsAsync(1)).PlanningMode);
        Assert.All(await settings.GetSeasonsAsync(1), season => Assert.Equal(SeasonStatus.Cancelled, season.Status));
        Assert.Equal(2, (await settings.GetSeasonsAsync(1)).Count);
    }

    [SeasonMongoFact]
    public async Task Coordinator_recovers_start_and_early_close_after_publication_failure()
    {
        var coordinator = Coordinator();
        var result = await planning.SetupAsync(Config(), 1, "request");
        var id = result.Seasons[0].Id!;
        realtime.FailNext = true;
        await using (await planning.AcquireLeaseAsync(1, default))
            await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.ActivateAsync(1, id));
        var started = (await settings.GetSeasonsAsync(1)).Single();
        Assert.True(started.BaselineInitialized);
        Assert.False(started.StartRealtimePublished);
        await coordinator.RunOnceAsync();
        Assert.True((await settings.GetSeasonsAsync(1)).Single().StartRealtimePublished);
        realtime.FailNext = true;
        await using (await planning.AcquireLeaseAsync(1, default))
            await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.CloseAsync(1, id));
        await coordinator.RunOnceAsync();
        var closed = (await settings.GetSeasonsAsync(1)).Single();
        Assert.Equal(SeasonStatus.Closed, closed.Status);
        Assert.True(closed.CloseRealtimePublished);
        Assert.Null(closed.ActiveGuildId);
    }

    [SeasonMongoFact]
    public async Task Future_and_expired_seasons_cannot_be_started_and_only_one_season_can_be_active()
    {
        await settings.SaveSettingsAsync(Config());
        var now = DateTime.UtcNow;
        var future = Instance(1, now.AddDays(1), now.AddDays(2));
        var expired = Instance(2, now.AddDays(-2), now.AddDays(-1));
        var current = Instance(3, now.AddHours(-1), now.AddHours(1));
        var overlap = Instance(4, now.AddHours(-1), now.AddHours(1));
        await database.GuildSeasons.InsertManyAsync([future, expired, current, overlap]);
        await using (await planning.AcquireLeaseAsync(1, default))
        {
            Assert.False(await lifecycle.ActivateAsync(1, future.Id!));
            Assert.False(await lifecycle.ActivateAsync(1, expired.Id!));
            Assert.True(await lifecycle.ActivateAsync(1, current.Id!));
            Assert.False(await lifecycle.ActivateAsync(1, overlap.Id!));
        }
        Assert.Single(await settings.GetSeasonsAsync(1), x => x.Status == SeasonStatus.Active);
    }

    [SeasonMongoFact]
    public async Task Cancelled_future_season_can_be_restored_without_starting_early()
    {
        await settings.SaveSettingsAsync(Config());
        var season = Instance(1, DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(2));
        await database.GuildSeasons.InsertOneAsync(season);
        await using (await planning.AcquireLeaseAsync(1, default))
        {
            Assert.True(await lifecycle.CancelAsync(1, season.Id!));
            Assert.True(await lifecycle.ResumeAsync(1, season.Id!));
        }
        var restored = Assert.Single(await settings.GetSeasonsAsync(1));
        Assert.Equal(SeasonStatus.Scheduled, restored.Status);
        Assert.Null(restored.ActiveGuildId);
        Assert.Null(restored.ClosedAtUtc);
    }

    [SeasonMongoFact]
    public async Task Cancelled_active_season_keeps_existing_xp_when_restored()
    {
        var result = await planning.SetupAsync(Config(), 1, "request");
        var id = result.Seasons[0].Id!;
        await using (await planning.AcquireLeaseAsync(1, default))
        {
            Assert.True(await lifecycle.ActivateAsync(1, id));
            await database.GuildSeasons.UpdateOneAsync(x => x.Id == id, Builders<GuildSeason>.Update.Set(x => x.CarryOverApplied, true));
            await database.SeasonMemberXp.InsertOneAsync(new() { GuildId = 1, SeasonId = id, UserId = 42, StartingXp = 100, TotalXp = 150, EarnedXp = 50, CarryOverApplied = true });
            Assert.True(await lifecycle.CancelAsync(1, id));
            Assert.True(await lifecycle.ResumeAsync(1, id));
        }
        var member = await database.SeasonMemberXp.Find(x => x.SeasonId == id).SingleAsync();
        Assert.Equal(150, member.TotalXp);
        Assert.Equal(100, member.StartingXp);
        Assert.Equal(SeasonStatus.Active, (await settings.GetSeasonsAsync(1)).Single().Status);
    }

    [SeasonMongoFact]
    public async Task One_broken_guild_does_not_block_other_guilds()
    {
        var broken = Config(); broken.GuildId = 2; broken.TimeZoneId = "Invalid/Zone"; broken.PlanningMode = SeasonPlanningMode.MaintainPreparedBuffer;
        await database.GuildSeasonSettings.InsertOneAsync(broken);
        await planning.SetupAsync(Config(), 1, "request");
        var status = await Coordinator().RunOnceAsync();
        Assert.NotNull(status.LastError);
        Assert.Equal(SeasonStatus.Active, (await settings.GetSeasonsAsync(1)).Single().Status);
    }

    private SeasonCoordinator Coordinator() => new(database, lifecycle, planning, new Errors(), new WorkerHealthRegistry(TimeProvider.System), TimeProvider.System, NullLogger<SeasonCoordinator>.Instance);
    private static GuildSeasonSettings Config() => new() { GuildId = 1, Enabled = true, TimeZoneId = "UTC", ScheduleKind = SeasonScheduleKind.FixedDuration, ScheduleAnchorUtc = DateTime.UtcNow.Date.AddDays(-1), FixedDurationDays = 7 };
    private static GuildSeason Instance(long sequence, DateTime start, DateTime end) => new() { Id = ObjectId.GenerateNewId().ToString(), GuildId = 1, Sequence = sequence, Name = $"Season {sequence}", StartsAtUtc = start, EndsAtUtc = end, SettingsSnapshot = Config() };
    private sealed class Reports : IReportWriter { public Task WriteAsync(ReportWrite report, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class Realtime : ILeaderboardRealtimePublisher
    {
        public bool FailNext { get; set; }
        public Task PublishGuildAsync(ulong guildId, CancellationToken cancellationToken = default) { if (FailNext) { FailNext = false; throw new InvalidOperationException("Simulated publication failure"); } return Task.CompletedTask; }
        public Task PublishMemberAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishSettingsAsync(GuildLeaderboardSettings settings, string? previousAlias = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Discord : IGuildDiscordContextResolver
    {
        public ValueTask<GuildDiscordContext?> ResolveAsync(ulong guildId, CancellationToken cancellationToken = default) => ValueTask.FromResult<GuildDiscordContext?>(null);
        public Task<IReadOnlyDictionary<ulong, GuildDiscordContext>> ResolveManyAsync(IEnumerable<ulong> guildIds, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<ulong, GuildDiscordContext>>(new Dictionary<ulong, GuildDiscordContext>());
    }
    private sealed class Errors : IOperationalErrorRecorder
    {
        public long PersistenceFailureCount => 0;
        public Task<bool> RecordAsync(OperationalErrorWrite error, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
