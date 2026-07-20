using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;

namespace Rankoon.Data.Xp;

public interface IXpService
{
    Task<GuildXpSettings> GetSettingsAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(GuildXpSettings settings, CancellationToken cancellationToken = default);
    Task<bool> GrantAsync(ulong guildId, ulong userId, string displayName, string source, decimal amount, string key, ulong? channelId = null, CancellationToken cancellationToken = default);
    Task<bool> GrantAsync(XpGrantRequest request, CancellationToken cancellationToken = default);
    Task<bool> ReverseGrantAsync(string originalGrantKey, string reversalGrantKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemberXp>> GetLeaderboardAsync(ulong guildId, int take, CancellationToken cancellationToken = default);
    Task<MemberXp?> GetMemberAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default);
    Task RecalculateTotalAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default);
}

public sealed record XpGrantRequest(ulong GuildId, ulong UserId, string DisplayName, string Source, decimal Amount, string GrantKey, DateTime OccurredAtUtc, ulong? ChannelId = null, DateTime? PeriodStartsAtUtc = null, DateTime? PeriodEndsAtUtc = null, string? ReversesGrantKey = null);

public sealed class XpService(RankoonDbContext database, ISeasonService seasons, IReportWriter reports, ILeaderboardRealtimePublisher realtime, TimeProvider timeProvider, ILogger<XpService> logger) : IXpService
{
    public async Task<GuildXpSettings> GetSettingsAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var settings = await database.GuildXpSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        return settings ?? new GuildXpSettings { GuildId = guildId };
    }

    public Task SaveSettingsAsync(GuildXpSettings settings, CancellationToken cancellationToken = default)
    {
        var updatedAt = timeProvider.GetUtcNow().UtcDateTime;
        settings.UpdatedAt = updatedAt;
        var update = Builders<GuildXpSettings>.Update
            .SetOnInsert(x => x.GuildId, settings.GuildId)
            .Set(x => x.Enabled, settings.Enabled)
            .Set(x => x.Message, settings.Message)
            .Set(x => x.Voice, settings.Voice)
            .Set(x => x.Reaction, settings.Reaction)
            .Set(x => x.EventInterest, settings.EventInterest)
            .Set(x => x.Thread, settings.Thread)
            .Set(x => x.ExcludedChannelIds, settings.ExcludedChannelIds)
            .Set(x => x.ExcludedCategoryIds, settings.ExcludedCategoryIds)
            .Set(x => x.ExcludedRoleIds, settings.ExcludedRoleIds)
            .Set(x => x.ChannelMultipliers, settings.ChannelMultipliers)
            .Set(x => x.LevelRoles, settings.LevelRoles)
            .Set(x => x.LevelUpChannelId, settings.LevelUpChannelId)
            .Set(x => x.UpdatedAt, updatedAt);
        return database.GuildXpSettings.UpdateOneAsync(x => x.GuildId == settings.GuildId, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    public async Task<bool> GrantAsync(ulong guildId, ulong userId, string displayName, string source, decimal amount, string key, ulong? channelId = null, CancellationToken cancellationToken = default)
        => await GrantAsync(new XpGrantRequest(guildId, userId, displayName, source, amount, key, timeProvider.GetUtcNow().UtcDateTime, channelId), cancellationToken);

    public async Task<bool> GrantAsync(XpGrantRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Amount == 0) return false;
        var occurredAtUtc = DateTime.SpecifyKind(request.OccurredAtUtc, DateTimeKind.Utc);
        var season = await seasons.ResolveAsync(request.GuildId, occurredAtUtc, cancellationToken);
        XpLedgerEntry ledger;
        try
        {
            ledger = new XpLedgerEntry
            {
                GrantKey = request.GrantKey, GuildId = request.GuildId, UserId = request.UserId, DisplayName = request.DisplayName, Source = request.Source,
                Amount = request.Amount, ChannelId = request.ChannelId, OccurredAtUtc = occurredAtUtc, CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
                SeasonId = season?.Id, PeriodStartsAtUtc = request.PeriodStartsAtUtc, PeriodEndsAtUtc = request.PeriodEndsAtUtc, ReversesGrantKey = request.ReversesGrantKey
            };
            await database.XpLedger.InsertOneAsync(ledger, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            ledger = await database.XpLedger.Find(x => x.GrantKey == request.GrantKey).FirstOrDefaultAsync(cancellationToken) ?? throw new InvalidOperationException("Duplicate ledger entry could not be read.");
            if (ledger.ProjectionStatus == SeasonProjectionStatus.Applied) return false;
        }
        await ProjectAsync(ledger, cancellationToken);
        await reports.WriteAsync(new(request.GuildId, ReportCategories.Activity, ReportNames.XpGranted, ReportOutcomes.Succeeded, request.Source, request.UserId, Metadata: new Dictionary<string, object?>
        {
            ["source"] = request.Source,
            ["amount"] = request.Amount,
            ["channelId"] = request.ChannelId,
            ["seasonId"] = ledger.SeasonId
        }, SubjectId: request.UserId, ChannelId: request.ChannelId), cancellationToken);
        logger.LogDebug("Granted {Amount} {Source} XP to {UserId} in {GuildId}", request.Amount, request.Source, request.UserId, request.GuildId);
        return true;
    }

    public async Task<bool> ReverseGrantAsync(string originalGrantKey, string reversalGrantKey, CancellationToken cancellationToken = default)
    {
        var original = await database.XpLedger.Find(x => x.GrantKey == originalGrantKey).FirstOrDefaultAsync(cancellationToken);
        if (original == null) return false;
        var reversal = new XpGrantRequest(original.GuildId, original.UserId, original.DisplayName, $"{original.Source}_reversal", -original.Amount, reversalGrantKey,
            timeProvider.GetUtcNow().UtcDateTime, original.ChannelId, ReversesGrantKey: originalGrantKey);
        // Preserve the original immutable season attribution even if a different season is currently active.
        var existing = await database.XpLedger.Find(x => x.GrantKey == reversalGrantKey).FirstOrDefaultAsync(cancellationToken);
        if (existing != null) { if (existing.ProjectionStatus == SeasonProjectionStatus.Pending) await ProjectAsync(existing, cancellationToken); return false; }
        var entry = new XpLedgerEntry { GrantKey = reversal.GrantKey, GuildId = reversal.GuildId, UserId = reversal.UserId, DisplayName = reversal.DisplayName, Source = reversal.Source, Amount = reversal.Amount, ChannelId = reversal.ChannelId, OccurredAtUtc = reversal.OccurredAtUtc, CreatedAt = reversal.OccurredAtUtc, SeasonId = original.SeasonId, ReversesGrantKey = originalGrantKey };
        try { await database.XpLedger.InsertOneAsync(entry, cancellationToken: cancellationToken); }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { return false; }
        await ProjectAsync(entry, cancellationToken);
        return true;
    }

    internal async Task ProjectAsync(XpLedgerEntry ledger, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entries = await database.XpLedger.Find(x => x.GuildId == ledger.GuildId && x.UserId == ledger.UserId).ToListAsync(cancellationToken);
        var earned = entries.Sum(x => x.Amount);
        var messageCount = entries.LongCount(x => x.Source == "message");
        var voiceSeconds = entries.Where(x => x.Source == "voice" && x.PeriodStartsAtUtc != null && x.PeriodEndsAtUtc != null).Sum(x => (long)(x.PeriodEndsAtUtc!.Value - x.PeriodStartsAtUtc!.Value).TotalSeconds);
        var member = await GetMemberAsync(ledger.GuildId, ledger.UserId, cancellationToken) ?? new MemberXp { GuildId = ledger.GuildId, UserId = ledger.UserId };
        var preference = await database.MemberLeaderboardPreferences.Find(x => x.GuildId == ledger.GuildId && x.UserId == ledger.UserId).FirstOrDefaultAsync(cancellationToken);
        member.DisplayName = ledger.DisplayName;
        member.EarnedXp = earned;
        member.TotalXp = member.ImportedMee6Xp + earned + member.ManualAdjustment;
        member.MessageCount = Math.Max(member.MessageCount, messageCount);
        member.VoiceSeconds = Math.Max(member.VoiceSeconds, voiceSeconds);
        member.IsCurrentMember = true;
        member.PublicLeaderboardVisible = preference?.PublicVisible ?? member.PublicLeaderboardVisible;
        member.UpdatedAt = now;
        await database.MemberXp.UpdateOneAsync(x => x.GuildId == ledger.GuildId && x.UserId == ledger.UserId,
            Builders<MemberXp>.Update
                .SetOnInsert(x => x.GuildId, ledger.GuildId)
                .SetOnInsert(x => x.UserId, ledger.UserId)
                .Set(x => x.DisplayName, member.DisplayName)
                .Set(x => x.EarnedXp, member.EarnedXp)
                .Set(x => x.TotalXp, member.TotalXp)
                .Set(x => x.MessageCount, member.MessageCount)
                .Set(x => x.VoiceSeconds, member.VoiceSeconds)
                .Set(x => x.IsCurrentMember, member.IsCurrentMember)
                .Set(x => x.PublicLeaderboardVisible, member.PublicLeaderboardVisible)
                .Set(x => x.UpdatedAt, member.UpdatedAt),
            new UpdateOptions { IsUpsert = true }, cancellationToken);

        if (ledger.SeasonId != null)
        {
            var season = await database.GuildSeasons.Find(x => x.Id == ledger.SeasonId).FirstOrDefaultAsync(cancellationToken);
            if (season != null && season.Status != SeasonStatus.Closed)
            {
                var seasonEntries = entries.Where(x => x.SeasonId == ledger.SeasonId).ToList();
                var seasonMember = await database.SeasonMemberXp.Find(x => x.SeasonId == ledger.SeasonId && x.UserId == ledger.UserId).FirstOrDefaultAsync(cancellationToken)
                    ?? new SeasonMemberXp { GuildId = ledger.GuildId, SeasonId = ledger.SeasonId, UserId = ledger.UserId, StartingXp = InitialXp(season.SettingsSnapshot, member) };
                seasonMember.DisplayName = ledger.DisplayName;
                seasonMember.EarnedXp = seasonEntries.Sum(x => x.Amount);
                seasonMember.TotalXp = seasonMember.StartingXp + seasonMember.EarnedXp + seasonMember.ManualAdjustment;
                seasonMember.MessageCount = seasonEntries.LongCount(x => x.Source == "message");
                seasonMember.VoiceSeconds = seasonEntries.Where(x => x.Source == "voice" && x.PeriodStartsAtUtc != null && x.PeriodEndsAtUtc != null).Sum(x => (long)(x.PeriodEndsAtUtc!.Value - x.PeriodStartsAtUtc!.Value).TotalSeconds);
                seasonMember.IsCurrentMember = member.IsCurrentMember;
                seasonMember.PublicLeaderboardVisible = member.PublicLeaderboardVisible;
                seasonMember.UpdatedAtUtc = now;
                await database.SeasonMemberXp.UpdateOneAsync(x => x.SeasonId == ledger.SeasonId && x.UserId == ledger.UserId,
                    Builders<SeasonMemberXp>.Update
                        .SetOnInsert(x => x.GuildId, ledger.GuildId)
                        .SetOnInsert(x => x.SeasonId, ledger.SeasonId)
                        .SetOnInsert(x => x.UserId, ledger.UserId)
                        .SetOnInsert(x => x.StartingXp, seasonMember.StartingXp)
                        .SetOnInsert(x => x.ManualAdjustment, seasonMember.ManualAdjustment)
                        .Set(x => x.DisplayName, seasonMember.DisplayName)
                        .Set(x => x.EarnedXp, seasonMember.EarnedXp)
                        .Set(x => x.TotalXp, seasonMember.TotalXp)
                        .Set(x => x.MessageCount, seasonMember.MessageCount)
                        .Set(x => x.VoiceSeconds, seasonMember.VoiceSeconds)
                        .Set(x => x.IsCurrentMember, seasonMember.IsCurrentMember)
                        .Set(x => x.PublicLeaderboardVisible, seasonMember.PublicLeaderboardVisible)
                        .Set(x => x.UpdatedAtUtc, seasonMember.UpdatedAtUtc),
                    new UpdateOptions { IsUpsert = true }, cancellationToken);
            }
        }
        var guildEntries = await database.XpLedger.Find(x => x.GuildId == ledger.GuildId).ToListAsync(cancellationToken);
        var stats = await database.GuildStats.Find(x => x.GuildId == ledger.GuildId).FirstOrDefaultAsync(cancellationToken) ?? new GuildStats { GuildId = ledger.GuildId };
        stats.XpAwarded = guildEntries.Sum(x => x.Amount);
        stats.Messages = guildEntries.LongCount(x => x.Source == "message"); stats.Reactions = guildEntries.LongCount(x => x.Source == "reaction");
        stats.Threads = guildEntries.LongCount(x => x.Source.StartsWith("thread", StringComparison.Ordinal)); stats.EventInterests = guildEntries.LongCount(x => x.Source == "event_interest");
        await database.GuildStats.UpdateOneAsync(x => x.GuildId == ledger.GuildId,
            Builders<GuildStats>.Update
                .SetOnInsert(x => x.GuildId, ledger.GuildId)
                .Set(x => x.XpAwarded, stats.XpAwarded)
                .Set(x => x.Messages, stats.Messages)
                .Set(x => x.Reactions, stats.Reactions)
                .Set(x => x.Threads, stats.Threads)
                .Set(x => x.EventInterests, stats.EventInterests),
            new UpdateOptions { IsUpsert = true }, cancellationToken);
        await database.XpLedger.UpdateOneAsync(x => x.Id == ledger.Id && x.ProjectionStatus == SeasonProjectionStatus.Pending, Builders<XpLedgerEntry>.Update.Set(x => x.ProjectionStatus, SeasonProjectionStatus.Applied).Set(x => x.ProjectedAtUtc, now), cancellationToken: cancellationToken);
        await realtime.PublishMemberAsync(ledger.GuildId, ledger.UserId, cancellationToken);
    }

    private static decimal InitialXp(GuildSeasonSettings settings, MemberXp member) => settings.InitialXpMode switch
    {
        SeasonInitialXpMode.Lifetime => member.TotalXp,
        SeasonInitialXpMode.LifetimePercentage => decimal.Round(member.TotalXp * settings.InitialXpPercentage / 100m, 2, MidpointRounding.AwayFromZero),
        _ => 0m
    };

    public async Task<IReadOnlyList<MemberXp>> GetLeaderboardAsync(ulong guildId, int take, CancellationToken cancellationToken = default) =>
        await database.MemberXp.Find(x => x.GuildId == guildId && x.IsCurrentMember)
            .SortByDescending(x => x.TotalXp).ThenBy(x => x.UserId)
            .Limit(Math.Clamp(take, 1, 100)).ToListAsync(cancellationToken);

    public async Task<MemberXp?> GetMemberAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default) => await database.MemberXp.Find(x => x.GuildId == guildId && x.UserId == userId).FirstOrDefaultAsync(cancellationToken);

    public Task RecalculateTotalAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default)
    {
        var update = new PipelineUpdateDefinition<MemberXp>(new BsonDocument[]
        {
            new BsonDocument("$set", new BsonDocument("total_xp", new BsonDocument("$add", new BsonArray
            {
                new BsonDocument("$ifNull", new BsonArray { "$imported_mee6_xp", 0 }),
                new BsonDocument("$ifNull", new BsonArray { "$earned_xp", 0 }),
                new BsonDocument("$ifNull", new BsonArray { "$manual_adjustment", 0 })
            })))
        });
        return database.MemberXp.UpdateOneAsync(x => x.GuildId == guildId && x.UserId == userId, update, cancellationToken: cancellationToken);
    }
}
