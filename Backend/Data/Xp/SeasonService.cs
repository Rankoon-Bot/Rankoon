using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;

namespace Rankoon.Data.Xp;

public interface ISeasonService
{
    Task<GuildSeasonSettings> GetSettingsAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(GuildSeasonSettings settings, CancellationToken cancellationToken = default);
    Task<GuildSeason?> ResolveAsync(ulong guildId, DateTime occurredAtUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuildSeason>> GetSeasonsAsync(ulong guildId, CancellationToken cancellationToken = default);
}

public interface ISeasonLifecycleService
{
    Task<bool> ActivateAsync(ulong guildId, string seasonId, CancellationToken cancellationToken = default);
    Task<bool> CloseAsync(ulong guildId, string seasonId, CancellationToken cancellationToken = default);
    Task<bool> CancelAsync(ulong guildId, string seasonId, CancellationToken cancellationToken = default);
    Task<bool> ResumeAsync(ulong guildId, string seasonId, CancellationToken cancellationToken = default);
}

/// <summary>Resolves persisted season instances only. XP sources must never infer a season from mutable settings.</summary>
public sealed class SeasonService(RankoonDbContext database, TimeProvider timeProvider) : ISeasonService
{
    public async Task<GuildSeasonSettings> GetSettingsAsync(ulong guildId, CancellationToken cancellationToken = default) =>
        await database.GuildSeasonSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken)
        ?? new GuildSeasonSettings { GuildId = guildId, UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime };

    public Task SaveSettingsAsync(GuildSeasonSettings settings, CancellationToken cancellationToken = default)
    {
        SeasonScheduleGenerator.Validate(settings);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var update = Builders<GuildSeasonSettings>.Update
            .SetOnInsert(x => x.GuildId, settings.GuildId)
            .Set(x => x.Enabled, settings.Enabled)
            .Set(x => x.DefaultLeaderboardScope, settings.DefaultLeaderboardScope)
            .Set(x => x.TimeZoneId, settings.TimeZoneId)
            .Set(x => x.ScheduleKind, settings.ScheduleKind)
            .Set(x => x.ScheduleAnchorUtc, settings.ScheduleAnchorUtc)
            .Set(x => x.FixedDurationDays, settings.FixedDurationDays)
            .Set(x => x.GapDays, settings.GapDays)
            .Set(x => x.PreparedSeasonCount, settings.PreparedSeasonCount)
            .Set(x => x.PauseBehavior, settings.PauseBehavior)
            .Set(x => x.PublicHistoryCount, settings.PublicHistoryCount)
            .Set(x => x.InitialXpMode, settings.InitialXpMode)
            .Set(x => x.InitialXpPercentage, settings.InitialXpPercentage)
            .Set(x => x.CarryOverMode, settings.CarryOverMode)
            .Set(x => x.CarryOverPercentage, settings.CarryOverPercentage)
            .Set(x => x.CarryOverMaximumXp, settings.CarryOverMaximumXp)
            .Set(x => x.AnnouncementChannelId, settings.AnnouncementChannelId)
            .Set(x => x.Announcements, settings.Announcements)
            .Set(x => x.WinnerCount, settings.WinnerCount)
            .Set(x => x.NameTemplate, settings.NameTemplate)
            .Set(x => x.Rotation, settings.Rotation)
            .Set(x => x.RotationOffset, settings.RotationOffset)
            .Set(x => x.SeasonLevelRoles, settings.SeasonLevelRoles)
            .Inc(x => x.Revision, 1)
            .Set(x => x.UpdatedAtUtc, now);
        return database.GuildSeasonSettings.UpdateOneAsync(x => x.GuildId == settings.GuildId, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    public async Task<GuildSeason?> ResolveAsync(ulong guildId, DateTime occurredAtUtc, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(guildId, cancellationToken);
        if (!settings.Enabled) return null;
        var instant = DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc);
        return await database.GuildSeasons.Find(x => x.GuildId == guildId && x.Status == SeasonStatus.Active && x.StartsAtUtc <= instant && instant < x.EndsAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GuildSeason>> GetSeasonsAsync(ulong guildId, CancellationToken cancellationToken = default) =>
        await database.GuildSeasons.Find(x => x.GuildId == guildId).SortByDescending(x => x.Sequence).ToListAsync(cancellationToken);
}

public sealed class SeasonLifecycleService(RankoonDbContext database, IReportWriter reports, ILeaderboardRealtimePublisher realtime, TimeProvider timeProvider) : ISeasonLifecycleService
{
    public async Task<bool> ActivateAsync(ulong guildId, string seasonId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            await database.GuildSeasons.UpdateOneAsync(x => x.GuildId == guildId && x.Id == seasonId && x.Status == SeasonStatus.Scheduled,
                Builders<GuildSeason>.Update.Set(x => x.Status, SeasonStatus.Active).Set(x => x.ActiveGuildId, guildId).Set(x => x.ActivatedAtUtc, now), cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { return false; }
        var season = await database.GuildSeasons.Find(x => x.GuildId == guildId && x.Id == seasonId && x.Status == SeasonStatus.Active).FirstOrDefaultAsync(cancellationToken);
        if (season == null) return false;
        await ContinueActivationAsync(season, now, cancellationToken);
        return true;
    }

    public async Task<bool> CloseAsync(ulong guildId, string seasonId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await database.GuildSeasons.UpdateOneAsync(x => x.GuildId == guildId && x.Id == seasonId && x.Status == SeasonStatus.Active,
            Builders<GuildSeason>.Update.Set(x => x.Status, SeasonStatus.Closing), cancellationToken: cancellationToken);
        var season = await database.GuildSeasons.Find(x => x.GuildId == guildId && x.Id == seasonId && x.Status == SeasonStatus.Closing).FirstOrDefaultAsync(cancellationToken);
        if (season == null) return false;
        await FinalizeAsync(season, now, cancellationToken);
        return true;
    }

    public async Task<bool> CancelAsync(ulong guildId, string seasonId, CancellationToken cancellationToken = default)
    {
        var result = await database.GuildSeasons.UpdateOneAsync(x => x.GuildId == guildId && x.Id == seasonId && (x.Status == SeasonStatus.Scheduled || x.Status == SeasonStatus.Active),
            Builders<GuildSeason>.Update.Set(x => x.Status, SeasonStatus.Cancelled).Unset(x => x.ActiveGuildId).Set(x => x.ClosedAtUtc, timeProvider.GetUtcNow().UtcDateTime), cancellationToken: cancellationToken);
        return result.ModifiedCount > 0;
    }

    public async Task<bool> ResumeAsync(ulong guildId, string seasonId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var result = await database.GuildSeasons.UpdateOneAsync(x => x.GuildId == guildId && x.Id == seasonId && x.Status == SeasonStatus.Cancelled && !x.CarryOverApplied && !x.Finalized && x.StartsAtUtc <= now && now < x.EndsAtUtc,
            Builders<GuildSeason>.Update.Set(x => x.Status, SeasonStatus.Active).Set(x => x.ActiveGuildId, guildId).Set(x => x.ActivatedAtUtc, now).Set(x => x.ClosedAtUtc, null), cancellationToken: cancellationToken);
        if (result.ModifiedCount == 0) return false;
        var season = await database.GuildSeasons.Find(x => x.Id == seasonId).FirstAsync(cancellationToken);
        await ContinueActivationAsync(season, now, cancellationToken);
        return true;
    }

    private async Task ContinueActivationAsync(GuildSeason season, DateTime now, CancellationToken cancellationToken)
    {
        await InitializeBaselineAsync(season, now, cancellationToken);
        await ApplyCarryOverAsync(season, now, cancellationToken);
        await ReportOnceAsync(season, "start_reported", ReportNames.SeasonStarted, cancellationToken);
        await PublishOnceAsync(season, "start_realtime_published", cancellationToken);
    }

    private async Task InitializeBaselineAsync(GuildSeason season, DateTime now, CancellationToken cancellationToken)
    {
        if (season.BaselineInitialized) return;
        var members = await database.MemberXp.Find(x => x.GuildId == season.GuildId).ToListAsync(cancellationToken);
        var writes = members.Select(member => new UpdateOneModel<SeasonMemberXp>(
            Builders<SeasonMemberXp>.Filter.Eq(x => x.SeasonId, season.Id) & Builders<SeasonMemberXp>.Filter.Eq(x => x.UserId, member.UserId),
            Builders<SeasonMemberXp>.Update.SetOnInsert(x => x.GuildId, season.GuildId).SetOnInsert(x => x.SeasonId, season.Id!).SetOnInsert(x => x.UserId, member.UserId).SetOnInsert(x => x.DisplayName, member.DisplayName).SetOnInsert(x => x.StartingXp, InitialXp(season.SettingsSnapshot, member.TotalXp)).SetOnInsert(x => x.TotalXp, InitialXp(season.SettingsSnapshot, member.TotalXp)).SetOnInsert(x => x.IsCurrentMember, member.IsCurrentMember).SetOnInsert(x => x.PublicLeaderboardVisible, member.PublicLeaderboardVisible).SetOnInsert(x => x.IsDevelopmentMock, member.IsDevelopmentMock).SetOnInsert(x => x.UpdatedAtUtc, now)) { IsUpsert = true }).ToList();
        if (writes.Count > 0) await database.SeasonMemberXp.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken);
        await database.GuildSeasons.UpdateOneAsync(x => x.Id == season.Id && !x.BaselineInitialized, Builders<GuildSeason>.Update.Set(x => x.BaselineInitialized, true), cancellationToken: cancellationToken);
    }

    private async Task ApplyCarryOverAsync(GuildSeason season, DateTime now, CancellationToken cancellationToken)
    {
        if (season.CarryOverApplied || season.PreviousSeasonId == null || season.SettingsSnapshot.CarryOverMode == SeasonCarryOverMode.None) return;
        var standings = await database.SeasonFinalStandings.Find(x => x.SeasonId == season.PreviousSeasonId).ToListAsync(cancellationToken);
        foreach (var standing in standings)
        {
            var value = decimal.Round(standing.TotalXp * season.SettingsSnapshot.CarryOverPercentage / 100m, 2, MidpointRounding.AwayFromZero);
            if (season.SettingsSnapshot.CarryOverMaximumXp is decimal maximum) value = Math.Min(value, maximum);
            var filter = Builders<SeasonMemberXp>.Filter.Eq(x => x.SeasonId, season.Id) & Builders<SeasonMemberXp>.Filter.Eq(x => x.UserId, standing.UserId) & Builders<SeasonMemberXp>.Filter.Ne(x => x.CarryOverApplied, true);
            var update = Builders<SeasonMemberXp>.Update.SetOnInsert(x => x.GuildId, season.GuildId).SetOnInsert(x => x.SeasonId, season.Id!).SetOnInsert(x => x.UserId, standing.UserId).SetOnInsert(x => x.DisplayName, standing.DisplayName).SetOnInsert(x => x.IsDevelopmentMock, standing.IsDevelopmentMock).SetOnInsert(x => x.UpdatedAtUtc, now).Inc(x => x.StartingXp, value).Inc(x => x.TotalXp, value).Set(x => x.CarryOverApplied, true);
            try { await database.SeasonMemberXp.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken); }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { await database.SeasonMemberXp.UpdateOneAsync(filter, update, cancellationToken: cancellationToken); }
        }
        var applied = await database.GuildSeasons.UpdateOneAsync(x => x.Id == season.Id && !x.CarryOverApplied, Builders<GuildSeason>.Update.Set(x => x.CarryOverApplied, true), cancellationToken: cancellationToken);
        if (applied.ModifiedCount > 0) await ReportOnceAsync(season, "carry_over_reported", ReportNames.SeasonCarryOverApplied, cancellationToken);
    }

    private async Task FinalizeAsync(GuildSeason season, DateTime now, CancellationToken cancellationToken)
    {
        var current = await database.GuildSeasons.Find(x => x.Id == season.Id).FirstOrDefaultAsync(cancellationToken);
        if (current == null) return;
        if (!current.Finalized)
        {
            var members = await database.SeasonMemberXp.Find(x => x.SeasonId == season.Id).SortByDescending(x => x.TotalXp).ThenBy(x => x.DisplayName).ThenBy(x => x.UserId).ToListAsync(cancellationToken);
            var writes = members.Select((member, index) => new UpdateOneModel<SeasonFinalStanding>(Builders<SeasonFinalStanding>.Filter.Eq(x => x.SeasonId, season.Id) & Builders<SeasonFinalStanding>.Filter.Eq(x => x.UserId, member.UserId), Builders<SeasonFinalStanding>.Update.SetOnInsert(x => x.SeasonId, season.Id!).SetOnInsert(x => x.GuildId, season.GuildId).SetOnInsert(x => x.UserId, member.UserId).SetOnInsert(x => x.DisplayName, member.DisplayName).SetOnInsert(x => x.Rank, index + 1).SetOnInsert(x => x.TotalXp, member.TotalXp).SetOnInsert(x => x.Level, Mee6LevelCurve.GetLevel(member.TotalXp)).SetOnInsert(x => x.MessageCount, member.MessageCount).SetOnInsert(x => x.VoiceSeconds, member.VoiceSeconds).SetOnInsert(x => x.PublicLeaderboardVisible, member.PublicLeaderboardVisible).SetOnInsert(x => x.IsDevelopmentMock, member.IsDevelopmentMock).SetOnInsert(x => x.FinalizedAtUtc, now)) { IsUpsert = true }).ToList();
            if (writes.Count > 0) await database.SeasonFinalStandings.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken);
            await database.GuildSeasons.UpdateOneAsync(x => x.Id == season.Id && !x.Finalized, Builders<GuildSeason>.Update.Set(x => x.Finalized, true).Set(x => x.Status, SeasonStatus.Closed).Unset(x => x.ActiveGuildId).Set(x => x.ClosedAtUtc, now), cancellationToken: cancellationToken);
        }
        await ReportOnceAsync(season, "close_reported", ReportNames.SeasonClosed, cancellationToken);
        await PublishOnceAsync(season, "close_realtime_published", cancellationToken);
    }

    private static decimal InitialXp(GuildSeasonSettings settings, decimal totalXp) => settings.InitialXpMode switch { SeasonInitialXpMode.Lifetime => totalXp, SeasonInitialXpMode.LifetimePercentage => decimal.Round(totalXp * settings.InitialXpPercentage / 100m, 2, MidpointRounding.AwayFromZero), _ => 0m };
    private async Task ReportOnceAsync(GuildSeason season, string field, string name, CancellationToken cancellationToken) { var filter = new MongoDB.Bson.BsonDocument { ["_id"] = new MongoDB.Bson.BsonObjectId(MongoDB.Bson.ObjectId.Parse(season.Id!)), [field] = new MongoDB.Bson.BsonDocument("$ne", true) }; var claimed = await database.GuildSeasons.UpdateOneAsync(filter, new MongoDB.Bson.BsonDocument("$set", new MongoDB.Bson.BsonDocument(field, true)), cancellationToken: cancellationToken); if (claimed.ModifiedCount > 0) await reports.WriteAsync(new(season.GuildId, ReportCategories.Activity, name, ReportOutcomes.Succeeded, Metadata: new Dictionary<string, object?> { ["seasonId"] = season.Id, ["sequence"] = season.Sequence }), cancellationToken); }
    private async Task PublishOnceAsync(GuildSeason season, string field, CancellationToken cancellationToken) { var filter = new MongoDB.Bson.BsonDocument { ["_id"] = new MongoDB.Bson.BsonObjectId(MongoDB.Bson.ObjectId.Parse(season.Id!)), [field] = new MongoDB.Bson.BsonDocument("$ne", true) }; var claimed = await database.GuildSeasons.UpdateOneAsync(filter, new MongoDB.Bson.BsonDocument("$set", new MongoDB.Bson.BsonDocument(field, true)), cancellationToken: cancellationToken); if (claimed.ModifiedCount > 0) await realtime.PublishGuildAsync(season.GuildId, cancellationToken); }
}
