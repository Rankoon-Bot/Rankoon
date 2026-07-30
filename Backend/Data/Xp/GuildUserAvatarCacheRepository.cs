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
        // Never replace an upserted document with Id == null: MongoDB would persist _id: null,
        // making every later cache insert collide on the mandatory _id index.
        var writes = entries.Select(entry => new UpdateOneModel<GuildUserAvatarCacheEntry>(
            Builders<GuildUserAvatarCacheEntry>.Filter.Eq(x => x.GuildId, entry.GuildId) & Builders<GuildUserAvatarCacheEntry>.Filter.Eq(x => x.UserId, entry.UserId),
            Builders<GuildUserAvatarCacheEntry>.Update
                .SetOnInsert(x => x.GuildId, entry.GuildId)
                .SetOnInsert(x => x.UserId, entry.UserId)
                // Set, rather than unset, retains the intentional "avatar removed" null state.
                .Set(x => x.AvatarId, entry.AvatarId)
                .Set(x => x.GuildAvatarId, entry.GuildAvatarId)
                .Set(x => x.DefaultAvatarIndex, entry.DefaultAvatarIndex)
                .Set(x => x.UpdatedAtUtc, entry.UpdatedAtUtc)
                .Set(x => x.LastObservedAtUtc, entry.LastObservedAtUtc)
                .Set(x => x.LastHydratedAtUtc, entry.LastHydratedAtUtc)
                .Set(x => x.NeedsHydration, entry.NeedsHydration)
                .Set(x => x.NextHydrationAttemptAtUtc, entry.NextHydrationAttemptAtUtc)
                .Set(x => x.HydrationAttemptCount, entry.HydrationAttemptCount)
                .Set(x => x.HydrationLeaseOwner, entry.HydrationLeaseOwner)
                .Set(x => x.HydrationLeaseUntilUtc, entry.HydrationLeaseUntilUtc)) { IsUpsert = true });
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
