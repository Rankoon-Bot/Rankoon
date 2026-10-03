using Discord.WebSocket;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Operations;

public sealed class BotGuildHistoryRecorder(RankoonDbContext database, TimeProvider timeProvider, ILogger<BotGuildHistoryRecorder> logger)
{
    private long persistenceFailures;
    public long PersistenceFailureCount => Interlocked.Read(ref persistenceFailures);
    public static string InstallationId(ulong botId, ulong guildId, DateTimeOffset joinedAt) => $"{botId}:{guildId}:{joinedAt.UtcTicks}";

    public Task ObserveAsync(ulong botId, string identity, SocketGuild guild, bool joined)
    {
        if (guild.CurrentUser?.JoinedAt is not { } joinedAt) return Task.CompletedTask; // Never invent an installation date.
        return ObserveMembershipAsync(botId, identity, guild.Id, guild.Name, guild.MemberCount, joinedAt, joined);
    }

    public async Task ObserveMembershipAsync(ulong botId, string identity, ulong guildId, string name, int members, DateTimeOffset joinedAt, bool joined)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var id = InstallationId(botId, guildId, joinedAt);
        var update = Builders<BotGuildInstallation>.Update
            .SetOnInsert(x => x.BotId, botId).SetOnInsert(x => x.GuildId, guildId)
            .SetOnInsert(x => x.JoinedAtUtc, joinedAt.UtcDateTime).SetOnInsert(x => x.FirstObservedAtUtc, now)
            .SetOnInsert(x => x.HistoryAtUtc, now)
            .Set(x => x.GuildName, name).Set(x => x.Identity, identity)
            .Set(x => x.MemberCount, members).Max(x => x.LastObservedAtUtc, now);
        update = joined ? update.Set(x => x.JoinObserved, true) : update.SetOnInsert(x => x.JoinObserved, false);
        try { await database.BotGuildInstallations.UpdateOneAsync(x => x.Id == id, update, new() { IsUpsert = true }); }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another callback may have inserted this same membership concurrently.
            await database.BotGuildInstallations.UpdateOneAsync(x => x.Id == id, update);
        }
    }

    public Task RemovedAsync(ulong botId, SocketGuild guild) => RemovedMembershipAsync(botId, guild.Id, guild.CurrentUser?.JoinedAt);

    public async Task RemovedMembershipAsync(ulong botId, ulong guildId, DateTimeOffset? joinedAt)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        // Conditional update makes repeated notifications harmless and preserves the first removal time.
        var filter = Builders<BotGuildInstallation>.Filter.Where(x => x.BotId == botId && x.GuildId == guildId && x.RemovedAtUtc == null);
        if (joinedAt is { } knownJoin)
            filter &= Builders<BotGuildInstallation>.Filter.Eq(x => x.Id, InstallationId(botId, guildId, knownJoin));
        await database.BotGuildInstallations.UpdateManyAsync(filter,
            Builders<BotGuildInstallation>.Update.Set(x => x.RemovedAtUtc, now).Set(x => x.RemovalEvidence, "observed").Set(x => x.HistoryAtUtc, now));
    }

    public async Task ReconcileAsync(DiscordSocketClient shard, int totalShards, string identity, ulong? assignedGuildId)
    {
        var botId = shard.CurrentUser.Id;
        var startedAt = timeProvider.GetUtcNow().UtcDateTime;
        // Guilds includes unavailable guilds: a temporary outage must not become an uninstall.
        var memberships = shard.Guilds.ToDictionary(x => x.Id, x => x.CurrentUser?.JoinedAt is { } joinedAt ? InstallationId(botId, x.Id, joinedAt) : null);
        foreach (var guild in shard.Guilds.Where(x => !assignedGuildId.HasValue || x.Id == assignedGuildId.Value))
            await ObserveAsync(botId, identity, guild, false);
        await ReconcileMissingAsync(botId, totalShards, shard.ShardId, memberships, startedAt, assignedGuildId);
    }

    public async Task ReconcileMissingAsync(ulong botId, int totalShards, int shardId, IReadOnlyDictionary<ulong, string?> memberships, DateTime startedAt, ulong? assignedGuildId = null)
    {
        if (totalShards < 1 || shardId < 0 || shardId >= totalShards) throw new ArgumentOutOfRangeException(nameof(shardId));
        var open = await database.BotGuildInstallations.Find(x => x.BotId == botId && x.RemovedAtUtc == null && x.LastObservedAtUtc < startedAt).ToListAsync();
        foreach (var installation in open.Where(x => (x.GuildId >> 22) % (ulong)totalShards == (ulong)shardId && (!assignedGuildId.HasValue || x.GuildId == assignedGuildId.Value)))
        {
            if (memberships.TryGetValue(installation.GuildId, out var current) && (current == null || current == installation.Id)) continue;
            // READY is a complete membership snapshot for this shard. Only the detection time is known.
            await database.BotGuildInstallations.UpdateOneAsync(x => x.Id == installation.Id && x.RemovedAtUtc == null && x.LastObservedAtUtc < startedAt,
                Builders<BotGuildInstallation>.Update.Set(x => x.RemovedAtUtc, startedAt).Set(x => x.RemovalEvidence, "detected").Set(x => x.HistoryAtUtc, startedAt));
        }
    }

    public async Task SafelyAsync(Func<Task> action)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { await action(); return; }
            catch (Exception exception)
            {
                Interlocked.Increment(ref persistenceFailures);
                logger.LogError(exception, "Bot server history persistence failed (attempt {Attempt})", attempt + 1);
                if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(attempt + 1));
            }
        }
    }
}
