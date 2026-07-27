using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.Operations;

namespace Rankoon.Data.MongoDb;

public sealed class AuthDataIntegrityException(string message) : Exception(message);

/// <summary>
/// Reconciles historical auth data before enforcing the indexes used by authentication.
/// This runs only at startup; request handlers never perform data cleanup.
/// </summary>
public sealed class AuthDataIntegrityInitializer(
    RankoonDbContext database,
    IWorkerHealthRegistry health,
    TimeProvider timeProvider,
    ILogger<AuthDataIntegrityInitializer> logger)
{
    internal const string WorkerName = "mongo-auth-data";
    internal const string DiscordIdIndexName = "discord_id_unique";
    internal const string TokenHashIndexName = "refresh_token_hash_unique";
    internal const string FamilyIndexName = "refresh_family";
    internal const string UserIndexName = "refresh_user";
    internal const string ExpiresIndexName = "refresh_expires_ttl";
    private const int BatchSize = 100;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var owner = Guid.NewGuid().ToString("N");
        if (!await TryAcquireLeaseAsync(owner, cancellationToken))
        {
            health.Report(WorkerName, WorkerHealthState.Degraded, "Startup migration is owned by another instance");
            throw new InvalidOperationException("Auth data startup migration is already in progress.");
        }

        try
        {
            health.Report(WorkerName, WorkerHealthState.Degraded, "Reconciling Discord users");
            await ReconcileDiscordUserDuplicatesAsync(owner, cancellationToken);
            health.Report(WorkerName, WorkerHealthState.Degraded, "Migrating legacy refresh tokens");
            await MigrateLegacyRefreshTokensAsync(owner, cancellationToken);
            await EnsureAuthIndexesAsync(owner, cancellationToken);
            await CompleteLeaseAsync(owner, cancellationToken);
            health.Report(WorkerName, WorkerHealthState.Healthy, "Indexes verified; legacy refresh tokens migrated");
            logger.LogInformation("MongoDB auth data integrity initialization completed");
        }
        catch
        {
            await MarkLeaseFailedAsync(owner, cancellationToken);
            health.Report(WorkerName, WorkerHealthState.Unhealthy, "Auth data integrity initialization failed");
            throw;
        }
    }

    private async Task ReconcileDiscordUserDuplicatesAsync(string owner, CancellationToken cancellationToken)
    {
        var validId = new BsonDocument("discord_id", new BsonDocument("$gt", string.Empty));
        var duplicateIds = await database.DiscordUsers.Aggregate()
            .Match(validId)
            .Group(new BsonDocument { { "_id", "$discord_id" }, { "count", new BsonDocument("$sum", 1) } })
            .Match(new BsonDocument("count", new BsonDocument("$gt", 1)))
            .Project(new BsonDocument("_id", 1))
            .ToListAsync(cancellationToken);

        foreach (var duplicate in duplicateIds)
        {
            await RenewLeaseAsync(owner, cancellationToken);
            var discordId = duplicate["_id"].AsString;
            var users = await database.DiscordUsers.Find(x => x.DiscordId == discordId)
                .SortByDescending(x => x.UpdatedAt).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);
            var survivor = users.FirstOrDefault();
            if (survivor?.Id is null || users.Any(user => user.Id is null))
                throw new AuthDataIntegrityException("Cannot reconcile Discord users with invalid document identifiers.");

            foreach (var duplicateUser in users.Skip(1))
            {
                // Refresh tokens are the only persisted reference to DiscordUser's Mongo id.
                await database.RefreshTokens.UpdateManyAsync(x => x.UserId == duplicateUser.Id,
                    Builders<RefreshToken>.Update.Set(x => x.UserId, survivor.Id), cancellationToken: cancellationToken);
                await database.DiscordUsers.DeleteOneAsync(x => x.Id == duplicateUser.Id, cancellationToken);
            }
        }
    }

    private async Task MigrateLegacyRefreshTokensAsync(string owner, CancellationToken cancellationToken)
    {
        var legacyFilter = new BsonDocument("token", new BsonDocument("$gt", string.Empty));
        while (true)
        {
            await RenewLeaseAsync(owner, cancellationToken);
            var batch = await database.RefreshTokens.Find(legacyFilter).Limit(BatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0) return;

            foreach (var token in batch)
            {
                if (token.Id is null || string.IsNullOrEmpty(token.Token))
                    throw new AuthDataIntegrityException("Cannot migrate a legacy refresh token with an invalid document identifier.");

                var hash = HashRefreshToken(token.Token);
                var collision = await database.RefreshTokens.Find(Builders<RefreshToken>.Filter.And(
                    Builders<RefreshToken>.Filter.Eq(x => x.TokenHash, hash),
                    Builders<RefreshToken>.Filter.Ne(x => x.Id, token.Id))).AnyAsync(cancellationToken);
                if (collision)
                    throw new AuthDataIntegrityException("Refresh token hash collision detected during legacy migration.");

                // The filter protects against a concurrent update; no plaintext token is logged or retained.
                var filter = Builders<RefreshToken>.Filter.And(
                    Builders<RefreshToken>.Filter.Eq(x => x.Id, token.Id),
                    Builders<RefreshToken>.Filter.Eq(x => x.Token, token.Token));
                var update = Builders<RefreshToken>.Update.Set(x => x.TokenHash, hash).Unset(x => x.Token);
                await database.RefreshTokens.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
            }
        }
    }

    private async Task EnsureAuthIndexesAsync(string owner, CancellationToken cancellationToken)
    {
        await RenewLeaseAsync(owner, cancellationToken);
        await EnsureIndexesAsync(database.DiscordUsers.Indexes,
        [
            new(Builders<DiscordUser>.IndexKeys.Ascending(x => x.DiscordId), new CreateIndexOptions<DiscordUser>
            {
                Name = DiscordIdIndexName, Unique = true, PartialFilterExpression = NonEmptyStringFilter("discord_id")
            })
        ], cancellationToken);
        await EnsureIndexesAsync(database.RefreshTokens.Indexes,
        [
            new(Builders<RefreshToken>.IndexKeys.Ascending(x => x.TokenHash), new CreateIndexOptions<RefreshToken>
            {
                Name = TokenHashIndexName, Unique = true, PartialFilterExpression = NonEmptyStringFilter("token_hash")
            }),
            new(Builders<RefreshToken>.IndexKeys.Ascending(x => x.FamilyId), new CreateIndexOptions { Name = FamilyIndexName }),
            new(Builders<RefreshToken>.IndexKeys.Ascending(x => x.UserId), new CreateIndexOptions { Name = UserIndexName }),
            new(Builders<RefreshToken>.IndexKeys.Ascending(x => x.ExpiresAt), new CreateIndexOptions { Name = ExpiresIndexName, ExpireAfter = TimeSpan.Zero })
        ], cancellationToken);
    }

    private static async Task EnsureIndexesAsync<T>(IMongoIndexManager<T> indexes, IEnumerable<CreateIndexModel<T>> expected, CancellationToken cancellationToken)
    {
        using var cursor = await indexes.ListAsync(cancellationToken);
        var existing = await cursor.ToListAsync(cancellationToken);
        foreach (var wanted in expected)
        {
            var name = wanted.Options.Name!;
            var key = ExpectedKey(name);
            foreach (var index in existing.Where(index => index["name"] != "_id_" && index.TryGetValue("key", out var value) && value == key && index["name"] != name).ToArray())
                await indexes.DropOneAsync(index["name"].AsString, cancellationToken);
            var current = existing.FirstOrDefault(index => index["name"] == name);
            if (current is not null && !Matches(current, key, name))
                await indexes.DropOneAsync(name, cancellationToken);
            await indexes.CreateOneAsync(wanted, cancellationToken: cancellationToken);
        }
    }

    private static BsonDocument ExpectedKey(string name) => name switch
    {
        DiscordIdIndexName => new BsonDocument("discord_id", 1),
        TokenHashIndexName => new BsonDocument("token_hash", 1),
        FamilyIndexName => new BsonDocument("family_id", 1),
        UserIndexName => new BsonDocument("user_id", 1),
        ExpiresIndexName => new BsonDocument("expires_at", 1),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static bool Matches(BsonDocument actual, BsonDocument key, string name)
    {
        if (!actual.TryGetValue("key", out var actualKey) || actualKey != key ||
            actual.GetValue("unique", false).ToBoolean() != (name is DiscordIdIndexName or TokenHashIndexName) ||
            actual.GetValue("partialFilterExpression", BsonNull.Value) != ((BsonValue?)ExpectedPartialFilter(name) ?? BsonNull.Value)) return false;
        var actualExpiry = actual.GetValue("expireAfterSeconds", BsonNull.Value);
        return name != ExpiresIndexName ? actualExpiry.IsBsonNull : !actualExpiry.IsBsonNull && actualExpiry.ToDouble() == 0;
    }

    private static BsonDocument? ExpectedPartialFilter(string name) => name switch
    {
        DiscordIdIndexName => NonEmptyStringFilter("discord_id"),
        TokenHashIndexName => NonEmptyStringFilter("token_hash"),
        _ => null
    };

    private async Task<bool> TryAcquireLeaseAsync(string owner, CancellationToken cancellationToken)
    {
        // MongoDB creates the unique _id index automatically; recreating it with
        // Unique=true is rejected by the server and prevents the lease from starting.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            var lease = await database.AuthDataMigrationLocks.FindOneAndUpdateAsync(
                Builders<AuthDataMigrationLock>.Filter.And(
                    Builders<AuthDataMigrationLock>.Filter.Eq(x => x.Id, AuthDataMigrationLock.SingletonId),
                    Builders<AuthDataMigrationLock>.Filter.Or(Builders<AuthDataMigrationLock>.Filter.Lte(x => x.ExpiresAtUtc, now), Builders<AuthDataMigrationLock>.Filter.Eq(x => x.Owner, owner))),
                Builders<AuthDataMigrationLock>.Update.Set(x => x.Owner, owner).Set(x => x.Status, "Running").Set(x => x.ExpiresAtUtc, now.Add(LeaseDuration)).Set(x => x.UpdatedAtUtc, now).SetOnInsert(x => x.Id, AuthDataMigrationLock.SingletonId),
                new FindOneAndUpdateOptions<AuthDataMigrationLock> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, cancellationToken);
            return lease?.Owner == owner;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    private async Task RenewLeaseAsync(string owner, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var result = await database.AuthDataMigrationLocks.UpdateOneAsync(x => x.Id == AuthDataMigrationLock.SingletonId && x.Owner == owner,
            Builders<AuthDataMigrationLock>.Update.Set(x => x.ExpiresAtUtc, now.Add(LeaseDuration)).Set(x => x.UpdatedAtUtc, now), cancellationToken: cancellationToken);
        if (result.MatchedCount != 1) throw new InvalidOperationException("Auth data migration lease was lost.");
    }

    private Task CompleteLeaseAsync(string owner, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return database.AuthDataMigrationLocks.UpdateOneAsync(x => x.Id == AuthDataMigrationLock.SingletonId && x.Owner == owner,
            Builders<AuthDataMigrationLock>.Update.Set(x => x.Status, "Completed").Set(x => x.CompletedAtUtc, now).Set(x => x.ExpiresAtUtc, now).Set(x => x.UpdatedAtUtc, now), cancellationToken: cancellationToken);
    }

    private Task MarkLeaseFailedAsync(string owner, CancellationToken cancellationToken) => database.AuthDataMigrationLocks.UpdateOneAsync(
        x => x.Id == AuthDataMigrationLock.SingletonId && x.Owner == owner,
        Builders<AuthDataMigrationLock>.Update.Set(x => x.Status, "Failed").Set(x => x.ExpiresAtUtc, timeProvider.GetUtcNow().UtcDateTime).Set(x => x.UpdatedAtUtc, timeProvider.GetUtcNow().UtcDateTime), cancellationToken: cancellationToken);

    internal static string HashRefreshToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    internal static BsonDocument NonEmptyStringFilter(string field) => new(field, new BsonDocument("$gt", string.Empty));
}
