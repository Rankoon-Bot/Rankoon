using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Rankoon.Backend.Tests;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Operations;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class LevelRolePersistenceTests : IAsyncLifetime
{
    private readonly string databaseName = $"rankoon_roles_test_{Guid.NewGuid():N}";
    private MongoClient? client;
    private RankoonDbContext database = null!;

    public Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("RANKOON_SEASON_TEST_MONGO");
        if (connection != null)
        {
            client = new MongoClient(connection);
            database = new(Options.Create(new MongoDbSettings { ConnectionString = connection, DatabaseName = databaseName }));
        }
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => client?.DropDatabaseAsync(databaseName) ?? Task.CompletedTask;

    [SeasonMongoFact]
    public async Task Abandoned_claim_is_recovered_once_and_old_owner_cannot_complete_it()
    {
        var now = DateTime.UtcNow;
        var entry = new LevelTransitionEvent { Status = LevelTransitionStatus.Processing, LeaseOwner = "old", LeaseExpiresAtUtc = now.AddMinutes(-1) };
        await database.LevelTransitionEvents.InsertOneAsync(entry);
        var eligible = Builders<LevelTransitionEvent>.Filter.Eq(x => x.Id, entry.Id) & LevelProgressionWorker.EligibleWork(now);
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => database.LevelTransitionEvents.FindOneAndUpdateAsync(eligible,
            Builders<LevelTransitionEvent>.Update.Set(x => x.LeaseOwner, $"new-{i}").Set(x => x.LeaseExpiresAtUtc, now.AddMinutes(2)),
            new FindOneAndUpdateOptions<LevelTransitionEvent> { ReturnDocument = ReturnDocument.After })));
        var claimed = Assert.Single(claims, x => x != null)!;
        var stale = await database.LevelTransitionEvents.UpdateOneAsync(x => x.Id == entry.Id && x.LeaseOwner == "old",
            Builders<LevelTransitionEvent>.Update.Set(x => x.Status, LevelTransitionStatus.Delivered));
        Assert.Equal(0, stale.ModifiedCount);
        Assert.Equal(claimed.LeaseOwner, (await database.LevelTransitionEvents.Find(x => x.Id == entry.Id).SingleAsync()).LeaseOwner);
    }

    [SeasonMongoFact]
    public async Task Disconnected_bot_is_retried_with_bounded_attempts_instead_of_completed()
    {
        var resolver = LevelRoleReliabilityTests.InterfaceStub.Create<IGuildDiscordContextResolver>((_, _) => ValueTask.FromResult<GuildDiscordContext?>(null));
        var errors = LevelRoleReliabilityTests.InterfaceStub.Create<IOperationalErrorRecorder>((_, _) => Task.FromResult(true));
        var worker = new LevelProgressionWorker(database, resolver, new LevelRoleService(resolver, null!), null!, null!, null!, null!, null!, errors,
            new WorkerHealthRegistry(TimeProvider.System), TimeProvider.System, NullLogger<LevelProgressionWorker>.Instance);
        var entry = new LevelTransitionEvent { GuildId = 1, UserId = 2, NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(-1) };
        await database.LevelTransitionEvents.InsertOneAsync(entry);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await worker.ProcessAsync(entry, default);
            entry = await database.LevelTransitionEvents.Find(x => x.Id == entry.Id).SingleAsync();
            Assert.Equal(attempt, entry.DeliveryAttempts);
            Assert.Null(entry.LeaseOwner);
            Assert.Equal(attempt == 5 ? LevelTransitionStatus.DeadLetter : LevelTransitionStatus.RetryScheduled, entry.Status);
            if (attempt < 5)
            {
                Assert.True(entry.NextAttemptAtUtc > DateTime.UtcNow);
                await database.LevelTransitionEvents.UpdateOneAsync(x => x.Id == entry.Id,
                    Builders<LevelTransitionEvent>.Update.Set(x => x.NextAttemptAtUtc, DateTime.UtcNow.AddMinutes(-1)));
            }
        }
    }

    [SeasonMongoFact]
    public async Task Stopped_seasons_do_not_attempt_to_recreate_roles()
    {
        var resolver = new UnexpectedDiscord();
        var service = new SeasonLevelRoleService(database, resolver, TimeProvider.System);
        foreach (var status in new[] { SeasonStatus.Cancelled, SeasonStatus.Closing, SeasonStatus.Closed, SeasonStatus.Scheduled })
        {
            var season = new GuildSeason { GuildId = 1, Status = status };
            await database.GuildSeasons.InsertOneAsync(season);
            var result = await service.SynchronizeAsync(1, season.Id!, 2);
            Assert.Empty(result.Added);
        }
        Assert.Equal(0, resolver.Calls);
    }

    private sealed class UnexpectedDiscord : IGuildDiscordContextResolver
    {
        public int Calls { get; private set; }
        public ValueTask<GuildDiscordContext?> ResolveAsync(ulong guildId, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Stopped seasons must not access Discord.");
        }
        public Task<IReadOnlyDictionary<ulong, GuildDiscordContext>> ResolveManyAsync(IEnumerable<ulong> guildIds, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Stopped seasons must not access Discord.");
    }
}
