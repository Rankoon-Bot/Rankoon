using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Xp;

public interface IXpProjectionCoordinator
{
    Task<IXpProjectionLease?> AcquireAsync(ulong guildId, ulong userId, string? seasonId, CancellationToken cancellationToken = default);
}

public interface IXpProjectionLease : IAsyncDisposable
{
    Task<bool> RenewAsync(CancellationToken cancellationToken = default);
    Task<bool> IsOwnedAsync(CancellationToken cancellationToken = default);
}

public sealed class XpProjectionCoordinator(RankoonDbContext database, TimeProvider timeProvider) : IXpProjectionCoordinator
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    public async Task<IXpProjectionLease?> AcquireAsync(ulong guildId, ulong userId, string? seasonId, CancellationToken cancellationToken = default)
    {
        var owner = Guid.NewGuid().ToString("N");
        var keys = new List<string> { $"guild:{guildId}", $"member:{guildId}:{userId}" };
        if (seasonId != null) keys.Add($"season:{seasonId}:{userId}");
        var acquired = new List<string>(keys.Count);
        foreach (var key in keys)
        {
            if (await TryAcquireAsync(key, guildId, userId, owner, cancellationToken))
            {
                acquired.Add(key);
                continue;
            }
            await ReleaseAsync(acquired, owner, cancellationToken);
            return null;
        }
        return new Lease(database.XpProjectionLeases, acquired, owner, timeProvider, LeaseDuration);
    }

    private async Task<bool> TryAcquireAsync(string key, ulong guildId, ulong userId, string owner, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var filter = Builders<XpProjectionLease>.Filter.Eq(x => x.Key, key) &
            (Builders<XpProjectionLease>.Filter.Lte(x => x.ExpiresAtUtc, now) | Builders<XpProjectionLease>.Filter.Eq(x => x.OwnerId, owner));
        var update = Builders<XpProjectionLease>.Update.SetOnInsert(x => x.Key, key).SetOnInsert(x => x.GuildId, guildId).SetOnInsert(x => x.UserId, userId)
            .Set(x => x.OwnerId, owner).Set(x => x.ExpiresAtUtc, now.Add(LeaseDuration));
        try
        {
            var result = await database.XpProjectionLeases.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
            return result.MatchedCount != 0 || result.UpsertedId != null;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { return false; }
    }

    private Task ReleaseAsync(IEnumerable<string> keys, string owner, CancellationToken cancellationToken) => database.XpProjectionLeases.DeleteManyAsync(
        Builders<XpProjectionLease>.Filter.In(x => x.Key, keys) & Builders<XpProjectionLease>.Filter.Eq(x => x.OwnerId, owner), cancellationToken);

    private sealed class Lease(IMongoCollection<XpProjectionLease> leases, IReadOnlyCollection<string> keys, string owner, TimeProvider timeProvider, TimeSpan duration) : IXpProjectionLease
    {
        public async Task<bool> RenewAsync(CancellationToken cancellationToken = default)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var result = await leases.UpdateManyAsync(Builders<XpProjectionLease>.Filter.In(x => x.Key, keys) & Builders<XpProjectionLease>.Filter.Eq(x => x.OwnerId, owner) & Builders<XpProjectionLease>.Filter.Gt(x => x.ExpiresAtUtc, now),
                Builders<XpProjectionLease>.Update.Set(x => x.ExpiresAtUtc, now.Add(duration)), cancellationToken: cancellationToken);
            return OwnsAll(keys.Count, result.MatchedCount);
        }

        public async Task<bool> IsOwnedAsync(CancellationToken cancellationToken = default)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var count = await leases.CountDocumentsAsync(Builders<XpProjectionLease>.Filter.In(x => x.Key, keys) & Builders<XpProjectionLease>.Filter.Eq(x => x.OwnerId, owner) & Builders<XpProjectionLease>.Filter.Gt(x => x.ExpiresAtUtc, now), cancellationToken: cancellationToken);
            return OwnsAll(keys.Count, count);
        }

        public async ValueTask DisposeAsync() => await leases.DeleteManyAsync(Builders<XpProjectionLease>.Filter.In(x => x.Key, keys) & Builders<XpProjectionLease>.Filter.Eq(x => x.OwnerId, owner));
    }

    internal static bool OwnsAll(int expectedKeys, long ownedKeys) => expectedKeys > 0 && ownedKeys == expectedKeys;
}
