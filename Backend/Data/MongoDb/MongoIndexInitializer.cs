using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using MongoDB.Bson;
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
                var obsoleteHoldbackFilter = Builders<GuildXpSettings>.Filter.Exists("Voice.HoldbackThreshold");
                var obsoleteHoldbackUpdate = Builders<GuildXpSettings>.Update.Unset("Voice.HoldbackThreshold");
                await database.GuildXpSettings.UpdateManyAsync(obsoleteHoldbackFilter, obsoleteHoldbackUpdate, cancellationToken: stoppingToken);
                await database.GuildXpSettings.Indexes.CreateOneAsync(new CreateIndexModel<GuildXpSettings>(Builders<GuildXpSettings>.IndexKeys.Ascending(x => x.GuildId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.GuildSeasonSettings.Indexes.CreateOneAsync(new CreateIndexModel<GuildSeasonSettings>(Builders<GuildSeasonSettings>.IndexKeys.Ascending(x => x.GuildId), new CreateIndexOptions { Unique = true, Name = "guild_unique" }), cancellationToken: stoppingToken);
                await database.GuildSeasons.Indexes.CreateManyAsync([
                    new CreateIndexModel<GuildSeason>(Builders<GuildSeason>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.Sequence), new CreateIndexOptions { Unique = true, Name = "guild_sequence_unique" }),
                    new CreateIndexModel<GuildSeason>(Builders<GuildSeason>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.Status).Ascending(x => x.StartsAtUtc).Ascending(x => x.EndsAtUtc), new CreateIndexOptions { Name = "guild_status_period" }),
                    new CreateIndexModel<GuildSeason>(Builders<GuildSeason>.IndexKeys.Ascending(x => x.ActiveGuildId), new CreateIndexOptions { Unique = true, Sparse = true, Name = "one_active_per_guild" })
                ], stoppingToken);
                await database.SeasonMemberXp.Indexes.CreateManyAsync([
                    new CreateIndexModel<SeasonMemberXp>(Builders<SeasonMemberXp>.IndexKeys.Ascending(x => x.SeasonId).Ascending(x => x.UserId), new CreateIndexOptions { Unique = true, Name = "season_user_unique" }),
                    new CreateIndexModel<SeasonMemberXp>(Builders<SeasonMemberXp>.IndexKeys.Ascending(x => x.SeasonId).Ascending(x => x.IsCurrentMember).Ascending(x => x.PublicLeaderboardVisible).Descending(x => x.TotalXp).Ascending(x => x.UserId), new CreateIndexOptions { Name = "season_public_ranking" }),
                    new CreateIndexModel<SeasonMemberXp>(Builders<SeasonMemberXp>.IndexKeys.Ascending(x => x.SeasonId).Ascending(x => x.IsCurrentMember).Descending(x => x.TotalXp).Ascending(x => x.UserId), new CreateIndexOptions { Name = "season_member_ranking" })
                ], stoppingToken);
                await database.SeasonFinalStandings.Indexes.CreateManyAsync([
                    new CreateIndexModel<SeasonFinalStanding>(Builders<SeasonFinalStanding>.IndexKeys.Ascending(x => x.SeasonId).Ascending(x => x.UserId), new CreateIndexOptions { Unique = true, Name = "season_user_unique" }),
                    new CreateIndexModel<SeasonFinalStanding>(Builders<SeasonFinalStanding>.IndexKeys.Ascending(x => x.SeasonId).Ascending(x => x.Rank), new CreateIndexOptions { Unique = true, Name = "season_rank_unique" })
                ], stoppingToken);
                await database.SeasonFinalStandings.UpdateManyAsync(new BsonDocument("public_leaderboard_visible", new BsonDocument("$exists", false)), Builders<SeasonFinalStanding>.Update.Set(x => x.PublicLeaderboardVisible, true), cancellationToken: stoppingToken);
                await database.SeasonCoordinatorLeases.Indexes.CreateOneAsync(new CreateIndexModel<SeasonCoordinatorLease>(Builders<SeasonCoordinatorLease>.IndexKeys.Ascending(x => x.GuildId), new CreateIndexOptions { Unique = true, Name = "guild_unique" }), cancellationToken: stoppingToken);
                await database.SeasonAnnouncementDeliveries.Indexes.CreateOneAsync(new CreateIndexModel<SeasonAnnouncementDelivery>(Builders<SeasonAnnouncementDelivery>.IndexKeys.Ascending(x => x.DeliveryKey), new CreateIndexOptions { Unique = true, Name = "delivery_key_unique" }), cancellationToken: stoppingToken);
                await database.MemberXp.Indexes.CreateOneAsync(new CreateIndexModel<MemberXp>(Builders<MemberXp>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UserId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.MemberXp.Indexes.CreateOneAsync(new CreateIndexModel<MemberXp>(Builders<MemberXp>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.IsCurrentMember).Ascending(x => x.PublicLeaderboardVisible).Descending(x => x.TotalXp).Ascending(x => x.UserId)), cancellationToken: stoppingToken);
                await database.MemberXp.Indexes.CreateOneAsync(new CreateIndexModel<MemberXp>(Builders<MemberXp>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.IsCurrentMember).Descending(x => x.TotalXp).Ascending(x => x.UserId)), cancellationToken: stoppingToken);
                await database.GuildLeaderboardSettings.Indexes.CreateOneAsync(new CreateIndexModel<GuildLeaderboardSettings>(Builders<GuildLeaderboardSettings>.IndexKeys.Ascending(x => x.GuildId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.GuildLeaderboardSettings.Indexes.CreateOneAsync(new CreateIndexModel<GuildLeaderboardSettings>(Builders<GuildLeaderboardSettings>.IndexKeys.Ascending(x => x.Alias), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.MemberLeaderboardPreferences.Indexes.CreateOneAsync(new CreateIndexModel<MemberLeaderboardPreference>(Builders<MemberLeaderboardPreference>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UserId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.XpLedger.Indexes.CreateOneAsync(new CreateIndexModel<XpLedgerEntry>(Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.GrantKey), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.XpLedger.Indexes.CreateManyAsync([
                    new CreateIndexModel<XpLedgerEntry>(Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.ProjectionStatus).Ascending(x => x.CreatedAt), new CreateIndexOptions { Name = "open_projection" }),
                    new CreateIndexModel<XpLedgerEntry>(Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.SeasonId).Ascending(x => x.OccurredAtUtc), new CreateIndexOptions { Name = "guild_season_occurred" }),
                    // Bounds member recovery scans by the guild/member prefix and preserves occurred-time ordering.
                    new CreateIndexModel<XpLedgerEntry>(Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UserId).Ascending(x => x.OccurredAtUtc), new CreateIndexOptions { Name = "guild_user_occurred" }),
                    // Supports deterministic repair of one member's projection in a single season.
                    new CreateIndexModel<XpLedgerEntry>(Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.SeasonId).Ascending(x => x.UserId), new CreateIndexOptions { Name = "guild_season_user" })
                ], stoppingToken);
                await database.VoiceSessions.Indexes.CreateOneAsync(new CreateIndexModel<VoiceSession>(Builders<VoiceSession>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UserId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.TemporaryVoiceChannels.Indexes.CreateOneAsync(new CreateIndexModel<TemporaryVoiceChannel>(Builders<TemporaryVoiceChannel>.IndexKeys.Ascending(x => x.ChannelId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.GuildStats.Indexes.CreateOneAsync(new CreateIndexModel<GuildStats>(Builders<GuildStats>.IndexKeys.Ascending(x => x.GuildId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.SelfRolePanels.Indexes.CreateManyAsync([
                    new CreateIndexModel<SelfRolePanel>(Builders<SelfRolePanel>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UpdatedAt), new CreateIndexOptions { Name = "guild_updated" }),
                    new CreateIndexModel<SelfRolePanel>(Builders<SelfRolePanel>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.MessageId), new CreateIndexOptions { Unique = true, Name = "guild_message_unique" })
                ], stoppingToken);
                await database.SelfRoleAssignments.Indexes.CreateOneAsync(new CreateIndexModel<SelfRoleAssignment>(Builders<SelfRoleAssignment>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.PanelId).Ascending(x => x.MappingId).Ascending(x => x.UserId), new CreateIndexOptions { Unique = true, Name = "panel_mapping_user_unique" }), cancellationToken: stoppingToken);
                await database.ReportEvents.Indexes.CreateManyAsync([
                    new CreateIndexModel<ReportEvent>(Builders<ReportEvent>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.Category).Descending(x => x.OccurredAt).Descending("_id"), new CreateIndexOptions { Name = "guild_category_occurred" }),
                    new CreateIndexModel<ReportEvent>(Builders<ReportEvent>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.Category).Ascending(x => x.Name).Descending(x => x.OccurredAt).Descending("_id"), new CreateIndexOptions { Name = "guild_category_name" }),
                    new CreateIndexModel<ReportEvent>(Builders<ReportEvent>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.Category).Ascending(x => x.Outcome).Descending(x => x.OccurredAt).Descending("_id"), new CreateIndexOptions { Name = "guild_category_outcome" }),
                    new CreateIndexModel<ReportEvent>(Builders<ReportEvent>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.Category).Ascending(x => x.Severity).Descending(x => x.OccurredAt).Descending("_id"), new CreateIndexOptions { Name = "guild_category_severity" }),
                    new CreateIndexModel<ReportEvent>(Builders<ReportEvent>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.Category).Ascending(x => x.CorrelationId).Descending(x => x.OccurredAt).Descending("_id"), new CreateIndexOptions { Name = "guild_category_correlation" }),
                    new CreateIndexModel<ReportEvent>(Builders<ReportEvent>.IndexKeys.Ascending(x => x.ExpiresAt), new CreateIndexOptions { Name = "expires_ttl", ExpireAfter = TimeSpan.Zero })
                ], stoppingToken);
                var migration = new PipelineUpdateDefinition<MemberXp>(new BsonDocument[]
                {
                    new BsonDocument("$set", new BsonDocument
                    {
                        { "total_xp", new BsonDocument("$add", new BsonArray
                            {
                                new BsonDocument("$ifNull", new BsonArray { "$imported_mee6_xp", 0 }),
                                new BsonDocument("$ifNull", new BsonArray { "$earned_xp", 0 }),
                                new BsonDocument("$ifNull", new BsonArray { "$manual_adjustment", 0 })
                            }) },
                        { "is_current_member", new BsonDocument("$ifNull", new BsonArray { "$is_current_member", true }) },
                        { "public_leaderboard_visible", new BsonDocument("$ifNull", new BsonArray { "$public_leaderboard_visible", true }) }
                    })
                });
                var missingLeaderboardFields = new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("total_xp", new BsonDocument("$exists", false)),
                    new BsonDocument("is_current_member", new BsonDocument("$exists", false)),
                    new BsonDocument("public_leaderboard_visible", new BsonDocument("$exists", false))
                });
                await database.MemberXp.UpdateManyAsync(missingLeaderboardFields, migration, cancellationToken: stoppingToken);
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
