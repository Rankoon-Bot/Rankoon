using System.Net;
using Discord;
using Discord.WebSocket;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Xp;
using System.Threading.Channels;

namespace Rankoon.Data.Discord;

public sealed class GuildMembershipService(IGuildDiscordContextResolver discord, RankoonDbContext database, ILeaderboardRealtimePublisher realtime, TimeProvider timeProvider, ILogger<GuildMembershipService> logger) : BackgroundService
{
    private readonly SemaphoreSlim reconciliationLock = new(1, 1);
    private readonly Channel<ulong> reconciliationQueue = Channel.CreateUnbounded<ulong>();
    private readonly HashSet<ulong> queuedGuilds = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextFullReconciliation = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= nextFullReconciliation)
                {
                    var guildIds = await database.GuildLeaderboardSettings.Distinct(x => x.GuildId, Builders<GuildLeaderboardSettings>.Filter.Empty).ToListAsync(stoppingToken);
                    foreach (var guildId in guildIds) QueueGuild(guildId);
                    nextFullReconciliation = DateTime.UtcNow.AddHours(12);
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                try
                {
                    var guildId = await reconciliationQueue.Reader.ReadAsync(timeout.Token);
                    lock (queuedGuilds) queuedGuilds.Remove(guildId);
                    await ReconcileGuildAsync(guildId, stoppingToken);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Guild membership reconciliation failed");
                await Task.Delay(TimeSpan.FromMinutes(10), timeProvider, stoppingToken);
            }
        }
    }

    public void QueueGuild(ulong guildId)
    {
        lock (queuedGuilds)
        {
            if (!queuedGuilds.Add(guildId)) return;
        }
        reconciliationQueue.Writer.TryWrite(guildId);
    }

    public async Task ReconcileGuildAsync(ulong guildId, CancellationToken cancellationToken)
    {
        await reconciliationLock.WaitAsync(cancellationToken);
        try
        {
            var context = await discord.ResolveAsync(guildId, cancellationToken);
            if (context == null) return;
            var userIds = await database.MemberXp.Find(x => x.GuildId == guildId && !x.IsDevelopmentMock).Project(x => x.UserId).ToListAsync(cancellationToken);
            var currentMembers = new HashSet<ulong>();
            foreach (var userId in userIds)
            {
                try
                {
                    if (await context.Client.Rest.GetGuildUserAsync(guildId, userId, new RequestOptions { CancelToken = cancellationToken }) != null)
                        currentMembers.Add(userId);
                }
                catch (global::Discord.Net.HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound) { }
            }
            if (userIds.Count == 0) return;
            var writes = userIds.Select(userId => new UpdateOneModel<MemberXp>(
                Builders<MemberXp>.Filter.Where(x => x.GuildId == guildId && x.UserId == userId),
                Builders<MemberXp>.Update.Set(x => x.IsCurrentMember, currentMembers.Contains(userId)).Set(x => x.UpdatedAt, DateTime.UtcNow))).ToList();
            await database.MemberXp.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken);
            var seasonWrites = userIds.Select(userId => new UpdateManyModel<SeasonMemberXp>(
                Builders<SeasonMemberXp>.Filter.Where(x => x.GuildId == guildId && x.UserId == userId),
                Builders<SeasonMemberXp>.Update.Set(x => x.IsCurrentMember, currentMembers.Contains(userId)).Set(x => x.UpdatedAtUtc, timeProvider.GetUtcNow().UtcDateTime))).ToList();
            if (seasonWrites.Count > 0) await database.SeasonMemberXp.BulkWriteAsync(seasonWrites, new BulkWriteOptions { IsOrdered = false }, cancellationToken);
            foreach (var userId in userIds) await realtime.PublishMemberAsync(guildId, userId, cancellationToken);
        }
        finally { reconciliationLock.Release(); }
    }

    public Task UserJoinedAsync(SocketGuildUser user) => SetMembershipAsync(user.Guild.Id, user.Id, true);
    public Task UserLeftAsync(SocketGuild guild, SocketUser user) => SetMembershipAsync(guild.Id, user.Id, false);
    private async Task SetMembershipAsync(ulong guildId, ulong userId, bool current)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await database.MemberXp.UpdateOneAsync(x => x.GuildId == guildId && x.UserId == userId, Builders<MemberXp>.Update.Set(x => x.IsCurrentMember, current).Set(x => x.UpdatedAt, now));
        await database.SeasonMemberXp.UpdateManyAsync(x => x.GuildId == guildId && x.UserId == userId, Builders<SeasonMemberXp>.Update.Set(x => x.IsCurrentMember, current).Set(x => x.UpdatedAtUtc, now));
        await realtime.PublishMemberAsync(guildId, userId);
    }
}
