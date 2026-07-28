using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Xp;

public interface IGuildUserAvatarCacheRepository
{
    Task<IReadOnlyDictionary<ulong, GuildUserAvatarCacheEntry>> GetManyAsync(ulong guildId, IReadOnlyCollection<ulong> userIds, CancellationToken cancellationToken);
    Task BulkUpsertAsync(IReadOnlyCollection<GuildUserAvatarCacheEntry> entries, CancellationToken cancellationToken);
    Task EnsureHydrationRequestedAsync(ulong guildId, IReadOnlyCollection<ulong> userIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<GuildUserAvatarCacheEntry>> ClaimHydrationBatchAsync(string workerId, int batchSize, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken);
}

public sealed class GuildUserAvatarCacheRepository(RankoonDbContext database, TimeProvider timeProvider) : IGuildUserAvatarCacheRepository
{
    public async Task<IReadOnlyDictionary<ulong, GuildUserAvatarCacheEntry>> GetManyAsync(ulong guildId, IReadOnlyCollection<ulong> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0) return new Dictionary<ulong, GuildUserAvatarCacheEntry>();
        var filter = Builders<GuildUserAvatarCacheEntry>.Filter.Eq(x => x.GuildId, guildId) & Builders<GuildUserAvatarCacheEntry>.Filter.In(x => x.UserId, userIds);
        return (await database.GuildUserAvatarCache.Find(filter).ToListAsync(cancellationToken)).ToDictionary(x => x.UserId);
    }

    public Task BulkUpsertAsync(IReadOnlyCollection<GuildUserAvatarCacheEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0) return Task.CompletedTask;
        var writes = entries.Select(entry => new ReplaceOneModel<GuildUserAvatarCacheEntry>(
            Builders<GuildUserAvatarCacheEntry>.Filter.Eq(x => x.GuildId, entry.GuildId) & Builders<GuildUserAvatarCacheEntry>.Filter.Eq(x => x.UserId, entry.UserId), entry) { IsUpsert = true });
        return database.GuildUserAvatarCache.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken);
    }

    public Task EnsureHydrationRequestedAsync(ulong guildId, IReadOnlyCollection<ulong> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0) return Task.CompletedTask;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var writes = userIds.Distinct().Select(userId => new UpdateOneModel<GuildUserAvatarCacheEntry>(
            Builders<GuildUserAvatarCacheEntry>.Filter.Eq(x => x.GuildId, guildId) & Builders<GuildUserAvatarCacheEntry>.Filter.Eq(x => x.UserId, userId),
            Builders<GuildUserAvatarCacheEntry>.Update
                .SetOnInsert(x => x.GuildId, guildId).SetOnInsert(x => x.UserId, userId)
                .SetOnInsert(x => x.UpdatedAtUtc, now).SetOnInsert(x => x.LastObservedAtUtc, now)
                .SetOnInsert(x => x.NeedsHydration, true).SetOnInsert(x => x.NextHydrationAttemptAtUtc, now)) { IsUpsert = true });
        return database.GuildUserAvatarCache.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken);
    }

    public async Task<IReadOnlyList<GuildUserAvatarCacheEntry>> ClaimHydrationBatchAsync(string workerId, int batchSize, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var ready = Builders<GuildUserAvatarCacheEntry>.Filter.Eq(x => x.NeedsHydration, true) &
            (Builders<GuildUserAvatarCacheEntry>.Filter.Lte(x => x.NextHydrationAttemptAtUtc, nowUtc) | Builders<GuildUserAvatarCacheEntry>.Filter.Eq(x => x.NextHydrationAttemptAtUtc, null)) &
            (Builders<GuildUserAvatarCacheEntry>.Filter.Lte(x => x.HydrationLeaseUntilUtc, nowUtc) | Builders<GuildUserAvatarCacheEntry>.Filter.Eq(x => x.HydrationLeaseUntilUtc, null));
        var candidates = await database.GuildUserAvatarCache.Find(ready).SortBy(x => x.NextHydrationAttemptAtUtc).Limit(batchSize).ToListAsync(cancellationToken);
        var claimed = new List<GuildUserAvatarCacheEntry>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var claimedEntry = await database.GuildUserAvatarCache.FindOneAndUpdateAsync(
                Builders<GuildUserAvatarCacheEntry>.Filter.Eq(x => x.Id, candidate.Id) & ready,
                Builders<GuildUserAvatarCacheEntry>.Update.Set(x => x.HydrationLeaseOwner, workerId).Set(x => x.HydrationLeaseUntilUtc, nowUtc.Add(leaseDuration)),
                new FindOneAndUpdateOptions<GuildUserAvatarCacheEntry> { ReturnDocument = ReturnDocument.After }, cancellationToken);
            if (claimedEntry != null) claimed.Add(claimedEntry);
        }
        return claimed;
    }
}
