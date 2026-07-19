using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Xp;

public interface IXpService
{
    Task<GuildXpSettings> GetSettingsAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(GuildXpSettings settings, CancellationToken cancellationToken = default);
    Task<bool> GrantAsync(ulong guildId, ulong userId, string displayName, string source, decimal amount, string key, ulong? channelId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemberXp>> GetLeaderboardAsync(ulong guildId, int take, CancellationToken cancellationToken = default);
    Task<MemberXp?> GetMemberAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default);
}

public sealed class XpService(RankoonDbContext database, ILogger<XpService> logger) : IXpService
{
    public async Task<GuildXpSettings> GetSettingsAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var settings = await database.GuildXpSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        return settings ?? new GuildXpSettings { GuildId = guildId };
    }

    public Task SaveSettingsAsync(GuildXpSettings settings, CancellationToken cancellationToken = default)
    {
        settings.UpdatedAt = DateTime.UtcNow;
        return database.GuildXpSettings.ReplaceOneAsync(x => x.GuildId == settings.GuildId, settings, new ReplaceOptions { IsUpsert = true }, cancellationToken);
    }

    public async Task<bool> GrantAsync(ulong guildId, ulong userId, string displayName, string source, decimal amount, string key, ulong? channelId = null, CancellationToken cancellationToken = default)
    {
        if (amount == 0) return false;
        try
        {
            await database.XpLedger.InsertOneAsync(new XpLedgerEntry { GrantKey = key, GuildId = guildId, UserId = userId, Source = source, Amount = amount, ChannelId = channelId }, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var update = Builders<MemberXp>.Update
            .SetOnInsert(x => x.GuildId, guildId).SetOnInsert(x => x.UserId, userId)
            .Set(x => x.DisplayName, displayName).Set(x => x.UpdatedAt, now)
            .Inc(x => x.EarnedXp, amount);
        if (source == "message") update = update.Inc(x => x.MessageCount, 1).Set(x => x.LastMessageXpAt, now);
        if (source == "reaction") update = update.Set(x => x.LastReactionXpAt, now);
        if (source == "thread_message") update = update.Set(x => x.LastThreadXpAt, now);
        await database.MemberXp.UpdateOneAsync(x => x.GuildId == guildId && x.UserId == userId, update, new UpdateOptions { IsUpsert = true }, cancellationToken);

        var statsUpdate = Builders<GuildStats>.Update.SetOnInsert(x => x.GuildId, guildId).Inc(x => x.XpAwarded, amount);
        if (source == "message") statsUpdate = statsUpdate.Inc(x => x.Messages, 1);
        if (source == "reaction") statsUpdate = statsUpdate.Inc(x => x.Reactions, 1);
        if (source.StartsWith("thread", StringComparison.Ordinal)) statsUpdate = statsUpdate.Inc(x => x.Threads, 1);
        if (source == "event_interest") statsUpdate = statsUpdate.Inc(x => x.EventInterests, 1);
        await database.GuildStats.UpdateOneAsync(x => x.GuildId == guildId, statsUpdate, new UpdateOptions { IsUpsert = true }, cancellationToken);
        logger.LogDebug("Granted {Amount} {Source} XP to {UserId} in {GuildId}", amount, source, userId, guildId);
        return true;
    }

    public async Task<IReadOnlyList<MemberXp>> GetLeaderboardAsync(ulong guildId, int take, CancellationToken cancellationToken = default) =>
        await database.MemberXp.Aggregate()
            .Match(x => x.GuildId == guildId)
            .AppendStage<MemberXp>(new BsonDocument("$addFields", new BsonDocument("_leaderboard_total_xp", new BsonDocument("$add", new BsonArray
            {
                new BsonDocument("$ifNull", new BsonArray { "$imported_mee6_xp", 0 }),
                new BsonDocument("$ifNull", new BsonArray { "$earned_xp", 0 }),
                new BsonDocument("$ifNull", new BsonArray { "$manual_adjustment", 0 })
            }))))
            .Sort(Builders<MemberXp>.Sort.Descending("_leaderboard_total_xp"))
            .Limit(Math.Clamp(take, 1, 100))
            .AppendStage<MemberXp>(new BsonDocument("$project", new BsonDocument("_leaderboard_total_xp", 0)))
            .ToListAsync(cancellationToken);

    public async Task<MemberXp?> GetMemberAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default) => await database.MemberXp.Find(x => x.GuildId == guildId && x.UserId == userId).FirstOrDefaultAsync(cancellationToken);
}
