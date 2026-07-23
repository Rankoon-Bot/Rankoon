using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using MongoDB.Bson;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;

namespace Rankoon.Data.MongoDb;

public sealed class MongoIndexInitializer(RankoonDbContext database, XpService xp, TimeProvider timeProvider, ILogger<MongoIndexInitializer> logger) : BackgroundService
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
                    new CreateIndexModel<SeasonMemberXp>(Builders<SeasonMemberXp>.IndexKeys.Ascending(x => x.SeasonId).Ascending(x => x.IsCurrentMember).Ascending(x => x.PublicLeaderboardVisible).Descending(x => x.TotalXp).Ascending(x => x.DisplayName).Ascending(x => x.UserId), new CreateIndexOptions { Name = "season_public_ranking_v2" }),
                    new CreateIndexModel<SeasonMemberXp>(Builders<SeasonMemberXp>.IndexKeys.Ascending(x => x.SeasonId).Ascending(x => x.IsCurrentMember).Descending(x => x.TotalXp).Ascending(x => x.DisplayName).Ascending(x => x.UserId), new CreateIndexOptions { Name = "season_member_ranking_v2" })
                ], stoppingToken);
                await database.SeasonFinalStandings.Indexes.CreateManyAsync([
                    new CreateIndexModel<SeasonFinalStanding>(Builders<SeasonFinalStanding>.IndexKeys.Ascending(x => x.SeasonId).Ascending(x => x.UserId), new CreateIndexOptions { Unique = true, Name = "season_user_unique" }),
                    new CreateIndexModel<SeasonFinalStanding>(Builders<SeasonFinalStanding>.IndexKeys.Ascending(x => x.SeasonId).Ascending(x => x.Rank), new CreateIndexOptions { Unique = true, Name = "season_rank_unique" }),
                    new CreateIndexModel<SeasonFinalStanding>(Builders<SeasonFinalStanding>.IndexKeys.Ascending(x => x.SeasonId).Ascending(x => x.PublicLeaderboardVisible).Ascending(x => x.Rank), new CreateIndexOptions { Name = "season_public_rank" })
                ], stoppingToken);
                await database.SeasonFinalStandings.UpdateManyAsync(new BsonDocument("public_leaderboard_visible", new BsonDocument("$exists", false)), Builders<SeasonFinalStanding>.Update.Set(x => x.PublicLeaderboardVisible, true), cancellationToken: stoppingToken);
                await database.SeasonCoordinatorLeases.Indexes.CreateOneAsync(new CreateIndexModel<SeasonCoordinatorLease>(Builders<SeasonCoordinatorLease>.IndexKeys.Ascending(x => x.GuildId), new CreateIndexOptions { Unique = true, Name = "guild_unique" }), cancellationToken: stoppingToken);
                await database.SeasonAnnouncementDeliveries.Indexes.CreateOneAsync(new CreateIndexModel<SeasonAnnouncementDelivery>(Builders<SeasonAnnouncementDelivery>.IndexKeys.Ascending(x => x.DeliveryKey), new CreateIndexOptions { Unique = true, Name = "delivery_key_unique" }), cancellationToken: stoppingToken);
                await database.MemberXp.Indexes.CreateOneAsync(new CreateIndexModel<MemberXp>(Builders<MemberXp>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UserId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.DevelopmentMockMembers.Indexes.CreateOneAsync(new CreateIndexModel<DevelopmentMockMember>(Builders<DevelopmentMockMember>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UserId), new CreateIndexOptions { Unique = true, Name = "guild_user_unique" }), cancellationToken: stoppingToken);
                await database.MemberXp.Indexes.CreateManyAsync([
                    new CreateIndexModel<MemberXp>(Builders<MemberXp>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.NormalizedDisplayName).Ascending(x => x.UserId), new CreateIndexOptions { Name = "guild_name_user" }),
                    new CreateIndexModel<MemberXp>(Builders<MemberXp>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.IsCurrentMember).Ascending(x => x.NormalizedDisplayName).Ascending(x => x.UserId), new CreateIndexOptions { Name = "guild_current_name_user" })
                ], stoppingToken);
                await database.MemberXp.Indexes.CreateOneAsync(new CreateIndexModel<MemberXp>(Builders<MemberXp>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.IsCurrentMember).Ascending(x => x.PublicLeaderboardVisible).Descending(x => x.TotalXp).Ascending(x => x.DisplayName).Ascending(x => x.UserId), new CreateIndexOptions { Name = "guild_public_ranking_v2" }), cancellationToken: stoppingToken);
                await database.MemberXp.Indexes.CreateOneAsync(new CreateIndexModel<MemberXp>(Builders<MemberXp>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.IsCurrentMember).Descending(x => x.TotalXp).Ascending(x => x.DisplayName).Ascending(x => x.UserId), new CreateIndexOptions { Name = "guild_member_ranking_v2" }), cancellationToken: stoppingToken);
                await database.GuildLeaderboardSettings.Indexes.CreateOneAsync(new CreateIndexModel<GuildLeaderboardSettings>(Builders<GuildLeaderboardSettings>.IndexKeys.Ascending(x => x.GuildId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.GuildLeaderboardSettings.Indexes.CreateOneAsync(new CreateIndexModel<GuildLeaderboardSettings>(Builders<GuildLeaderboardSettings>.IndexKeys.Ascending(x => x.Alias), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.MemberLeaderboardPreferences.Indexes.CreateOneAsync(new CreateIndexModel<MemberLeaderboardPreference>(Builders<MemberLeaderboardPreference>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UserId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.XpLedger.Indexes.CreateOneAsync(new CreateIndexModel<XpLedgerEntry>(Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.GrantKey), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.XpLedger.Indexes.CreateManyAsync([
                    new CreateIndexModel<XpLedgerEntry>(Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.ProjectionStatus).Ascending(x => x.CreatedAt), new CreateIndexOptions { Name = "open_projection" }),
                    new CreateIndexModel<XpLedgerEntry>(Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.SeasonId).Ascending(x => x.OccurredAtUtc), new CreateIndexOptions { Name = "guild_season_occurred" }),
                    // Bounds member recovery scans by the guild/member prefix and preserves occurred-time ordering.
                    new CreateIndexModel<XpLedgerEntry>(Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UserId).Descending(x => x.OccurredAtUtc).Descending("_id"), new CreateIndexOptions { Name = "guild_user_occurred_desc" }),
                    // Supports deterministic repair of one member's projection in a single season.
                    new CreateIndexModel<XpLedgerEntry>(Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.SeasonId).Ascending(x => x.UserId).Descending(x => x.OccurredAtUtc).Descending("_id"), new CreateIndexOptions { Name = "guild_season_user_occurred" }),
                    new CreateIndexModel<XpLedgerEntry>(Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.Kind).Ascending(x => x.ActorUserId).Descending(x => x.OccurredAtUtc).Descending("_id"), new CreateIndexOptions { Name = "guild_kind_actor_occurred" }),
                    new CreateIndexModel<XpLedgerEntry>(Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.ReversesLedgerEntryId), new CreateIndexOptions { Name = "reverses_ledger_entry_unique", Unique = true, Sparse = true })
                ], stoppingToken);
                await database.GuildLevelUpAnnouncementSettings.Indexes.CreateOneAsync(new CreateIndexModel<GuildLevelUpAnnouncementSettings>(Builders<GuildLevelUpAnnouncementSettings>.IndexKeys.Ascending(x => x.GuildId), new CreateIndexOptions { Unique = true, Name = "guild_unique" }), cancellationToken: stoppingToken);
                await database.LevelTransitionEvents.Indexes.CreateManyAsync([
                    new CreateIndexModel<LevelTransitionEvent>(Builders<LevelTransitionEvent>.IndexKeys.Ascending(x => x.EventKey), new CreateIndexOptions { Unique = true, Name = "event_key_unique" }),
                    new CreateIndexModel<LevelTransitionEvent>(Builders<LevelTransitionEvent>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.NextAttemptAtUtc), new CreateIndexOptions { Name = "open_delivery" }),
                    new CreateIndexModel<LevelTransitionEvent>(Builders<LevelTransitionEvent>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UserId).Descending(x => x.CreatedAtUtc), new CreateIndexOptions { Name = "guild_user_recent" })
                ], stoppingToken);
                await database.VoiceSessions.Indexes.CreateOneAsync(new CreateIndexModel<VoiceSession>(Builders<VoiceSession>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UserId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.TemporaryVoiceChannels.Indexes.CreateOneAsync(new CreateIndexModel<TemporaryVoiceChannel>(Builders<TemporaryVoiceChannel>.IndexKeys.Ascending(x => x.ChannelId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.GuildStats.Indexes.CreateOneAsync(new CreateIndexModel<GuildStats>(Builders<GuildStats>.IndexKeys.Ascending(x => x.GuildId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await database.SelfRolePanels.Indexes.CreateManyAsync([
                    new CreateIndexModel<SelfRolePanel>(Builders<SelfRolePanel>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UpdatedAt), new CreateIndexOptions { Name = "guild_updated" }),
                    new CreateIndexModel<SelfRolePanel>(Builders<SelfRolePanel>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.MessageId), new CreateIndexOptions { Unique = true, Name = "guild_message_unique" })
                ], stoppingToken);
                await database.SelfRoleAssignments.Indexes.CreateOneAsync(new CreateIndexModel<SelfRoleAssignment>(Builders<SelfRoleAssignment>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.PanelId).Ascending(x => x.MappingId).Ascending(x => x.UserId), new CreateIndexOptions { Unique = true, Name = "panel_mapping_user_unique" }), cancellationToken: stoppingToken);
                await database.GuildBotIdentities.Indexes.CreateManyAsync([
                    new CreateIndexModel<GuildBotIdentity>(Builders<GuildBotIdentity>.IndexKeys.Ascending(x => x.GuildId), new CreateIndexOptions { Unique = true, Name = "guild_unique" }),
                    new CreateIndexModel<GuildBotIdentity>(Builders<GuildBotIdentity>.IndexKeys.Ascending(x => x.TokenFingerprint), new CreateIndexOptions { Unique = true, Sparse = true, Name = "token_fingerprint_unique" }),
                    new CreateIndexModel<GuildBotIdentity>(Builders<GuildBotIdentity>.IndexKeys.Ascending(x => x.ApplicationId), new CreateIndexOptions { Unique = true, Sparse = true, Name = "application_unique" }),
                    new CreateIndexModel<GuildBotIdentity>(Builders<GuildBotIdentity>.IndexKeys.Ascending(x => x.Status).Descending(x => x.UpdatedAt), new CreateIndexOptions { Name = "status_updated" })
                ], stoppingToken);
                await database.CustomBotCapacityReservations.Indexes.CreateManyAsync([
                    new CreateIndexModel<CustomBotCapacityReservation>(Builders<CustomBotCapacityReservation>.IndexKeys.Ascending(x => x.GuildId), new CreateIndexOptions { Unique = true, Name = "guild_unique" }),
                    new CreateIndexModel<CustomBotCapacityReservation>(Builders<CustomBotCapacityReservation>.IndexKeys.Ascending(x => x.IdentityId), new CreateIndexOptions { Unique = true, Name = "identity_unique" }),
                    new CreateIndexModel<CustomBotCapacityReservation>(Builders<CustomBotCapacityReservation>.IndexKeys.Ascending(x => x.ReservedAtUtc), new CreateIndexOptions { Name = "reserved_at" })
                ], stoppingToken);
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
                var normalizeNames = new PipelineUpdateDefinition<MemberXp>(new BsonDocument[]
                {
                    new("$set", new BsonDocument("normalized_display_name", new BsonDocument("$toLower", new BsonDocument("$trim", new BsonDocument("input", new BsonDocument("$ifNull", new BsonArray { "$display_name", string.Empty }))))))
                });
                // Earlier versions accidentally stored the aggregation expression itself as a document.
                var invalidNormalizedName = new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("normalized_display_name", new BsonDocument("$exists", false)),
                    new BsonDocument("normalized_display_name", new BsonDocument("$type", "object"))
                });
                await database.MemberXp.UpdateManyAsync(invalidNormalizedName, normalizeNames, cancellationToken: stoppingToken);
                await MigrateLegacyManualAdjustmentsAsync(stoppingToken);
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

    private async Task MigrateLegacyManualAdjustmentsAsync(CancellationToken cancellationToken)
    {
        // Deterministic grant keys make an interrupted migration safe to resume.
        var members = await database.MemberXp.Find(x => x.ManualAdjustment != 0).ToListAsync(cancellationToken);
        foreach (var member in members)
        {
            var key = $"migration:manual-adjustment:v1:{member.GuildId}:{member.UserId}";
            if (await database.XpLedger.Find(x => x.GrantKey == key).AnyAsync(cancellationToken)) continue;
            var amount = member.ManualAdjustment;
            await database.MemberXp.UpdateOneAsync(x => x.Id == member.Id, Builders<MemberXp>.Update.Set(x => x.ManualAdjustment, 0m).Set(x => x.TotalXp, member.ImportedMee6Xp + member.EarnedXp), cancellationToken: cancellationToken);
            var entry = new XpLedgerEntry { GrantKey = key, GuildId = member.GuildId, UserId = member.UserId, DisplayName = member.DisplayName, Source = "legacy_manual_adjustment", Amount = amount, Kind = XpLedgerEntryKind.SystemMigration, Scope = XpLedgerScope.LifetimeOnly, Reason = "Migration of the legacy lifetime manual adjustment", CreatedAt = timeProvider.GetUtcNow().UtcDateTime, OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime };
            try { await database.XpLedger.InsertOneAsync(entry, cancellationToken: cancellationToken); await xp.ProjectAsync(entry, cancellationToken); } catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        }
        var seasonMembers = await database.SeasonMemberXp.Find(x => x.ManualAdjustment != 0).ToListAsync(cancellationToken);
        foreach (var member in seasonMembers)
        {
            var key = $"migration:season-manual-adjustment:v1:{member.SeasonId}:{member.UserId}";
            if (await database.XpLedger.Find(x => x.GrantKey == key).AnyAsync(cancellationToken)) continue;
            var amount = member.ManualAdjustment;
            await database.SeasonMemberXp.UpdateOneAsync(x => x.Id == member.Id, Builders<SeasonMemberXp>.Update.Set(x => x.ManualAdjustment, 0m).Set(x => x.TotalXp, member.StartingXp + member.EarnedXp), cancellationToken: cancellationToken);
            var entry = new XpLedgerEntry { GrantKey = key, GuildId = member.GuildId, UserId = member.UserId, DisplayName = member.DisplayName, Source = "legacy_season_manual_adjustment", Amount = amount, Kind = XpLedgerEntryKind.SystemMigration, Scope = XpLedgerScope.SeasonOnly, SeasonId = member.SeasonId, Reason = "Migration of the legacy season manual adjustment", CreatedAt = timeProvider.GetUtcNow().UtcDateTime, OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime };
            try { await database.XpLedger.InsertOneAsync(entry, cancellationToken: cancellationToken); await xp.ProjectAsync(entry, cancellationToken); } catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        }
    }
}
