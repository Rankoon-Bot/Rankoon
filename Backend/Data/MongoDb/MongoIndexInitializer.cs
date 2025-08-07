using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Rankoon.Data.Model;

namespace Rankoon.Data.MongoDb;

public sealed class MongoIndexInitializer(RankoonDbContext database, TimeProvider timeProvider, ILogger<MongoIndexInitializer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await database.GuildXpSettings.Indexes.CreateOneAsync(new CreateIndexModel<GuildXpSettings>(Builders<GuildXpSettings>.IndexKeys.Ascending(x => x.GuildId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.MemberXp.Indexes.CreateOneAsync(new CreateIndexModel<MemberXp>(Builders<MemberXp>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UserId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.XpLedger.Indexes.CreateOneAsync(new CreateIndexModel<XpLedgerEntry>(Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.GrantKey), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.VoiceSessions.Indexes.CreateOneAsync(new CreateIndexModel<VoiceSession>(Builders<VoiceSession>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UserId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.TemporaryVoiceChannels.Indexes.CreateOneAsync(new CreateIndexModel<TemporaryVoiceChannel>(Builders<TemporaryVoiceChannel>.IndexKeys.Ascending(x => x.ChannelId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.GuildStats.Indexes.CreateOneAsync(new CreateIndexModel<GuildStats>(Builders<GuildStats>.IndexKeys.Ascending(x => x.GuildId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                logger.LogInformation("MongoDB indexes initialized");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "MongoDB index initialization failed; retrying in 30 seconds");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
