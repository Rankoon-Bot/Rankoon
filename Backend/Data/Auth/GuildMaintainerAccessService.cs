using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Auth;

public interface IGuildMaintainerAccessService
{
    Task<GuildMaintainerAccess> GetAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<ulong, GuildMaintainerAccess>> GetManyAsync(IEnumerable<ulong> guildIds, CancellationToken cancellationToken = default);
    Task<GuildMaintainerAccess?> SetAsync(ulong guildId, bool enabled, long expectedRevision, ulong actorUserId, CancellationToken cancellationToken = default);
    Task<bool> IsEnabledAsync(ulong guildId, CancellationToken cancellationToken = default);
}

public sealed class GuildMaintainerAccessService(RankoonDbContext database, TimeProvider timeProvider) : IGuildMaintainerAccessService
{
    public async Task<GuildMaintainerAccess> GetAsync(ulong guildId, CancellationToken cancellationToken = default) =>
        await database.GuildMaintainerAccess.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken)
        ?? new GuildMaintainerAccess { GuildId = guildId };

    public async Task<IReadOnlyDictionary<ulong, GuildMaintainerAccess>> GetManyAsync(IEnumerable<ulong> guildIds, CancellationToken cancellationToken = default)
    {
        var ids = guildIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<ulong, GuildMaintainerAccess>();
        var settings = await database.GuildMaintainerAccess.Find(x => ids.Contains(x.GuildId)).ToListAsync(cancellationToken);
        var byGuild = settings.ToDictionary(x => x.GuildId);
        return ids.ToDictionary(id => id, id => byGuild.GetValueOrDefault(id) ?? new GuildMaintainerAccess { GuildId = id });
    }

    public async Task<GuildMaintainerAccess?> SetAsync(ulong guildId, bool enabled, long expectedRevision, ulong actorUserId, CancellationToken cancellationToken = default)
    {
        if (expectedRevision < 0) return null;
        var update = Builders<GuildMaintainerAccess>.Update
            .Set(x => x.Enabled, enabled)
            .Set(x => x.UpdatedByUserId, actorUserId)
            .Set(x => x.UpdatedAtUtc, timeProvider.GetUtcNow().UtcDateTime)
            .Inc(x => x.Revision, 1);
        try
        {
            return await database.GuildMaintainerAccess.FindOneAndUpdateAsync(
                x => x.GuildId == guildId && x.Revision == expectedRevision,
                update,
                new FindOneAndUpdateOptions<GuildMaintainerAccess> { IsUpsert = expectedRevision == 0, ReturnDocument = ReturnDocument.After },
                cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return null;
        }
    }

    public async Task<bool> IsEnabledAsync(ulong guildId, CancellationToken cancellationToken = default) =>
        (await GetAsync(guildId, cancellationToken)).Enabled;
}
