using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;

namespace Rankoon.Data.Xp;

public sealed record SeasonCoordinatorStatus(DateTimeOffset? LastRunAt, string? LastError);

public sealed class SeasonCoordinator(RankoonDbContext database, IReportWriter reports, ILeaderboardRealtimePublisher realtime, TimeProvider timeProvider, ILogger<SeasonCoordinator> logger) : BackgroundService
{
    private readonly string instanceId = Guid.NewGuid().ToString("N");
    private volatile SeasonCoordinatorStatus status = new(null, null);
    public SeasonCoordinatorStatus Status => status;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                status = new(timeProvider.GetUtcNow(), null);
                await Task.Delay(TimeSpan.FromMinutes(1), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Season coordinator failed");
                status = new(timeProvider.GetUtcNow(), exception.GetBaseException().GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var enabledGuilds = await database.GuildSeasonSettings.Find(x => x.Enabled).Project(x => x.GuildId).ToListAsync(cancellationToken);
        foreach (var guildId in enabledGuilds) await RunGuildAsync(guildId, cancellationToken);
    }

    public async Task<bool> CloseSeasonAsync(ulong guildId, string seasonId, CancellationToken cancellationToken = default)
    {
        var season = await database.GuildSeasons.Find(x => x.GuildId == guildId && x.Id == seasonId && (x.Status == SeasonStatus.Active || x.Status == SeasonStatus.Closing)).FirstOrDefaultAsync(cancellationToken);
        if (season == null) return false;
        await CloseAsync(season, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        return true;
    }

    private async Task RunGuildAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!await TryAcquireLeaseAsync(guildId, now, cancellationToken)) return;
        var all = await database.GuildSeasons.Find(x => x.GuildId == guildId).SortBy(x => x.Sequence).ToListAsync(cancellationToken);
        foreach (var expired in all.Where(x => x.Status is SeasonStatus.Active or SeasonStatus.Closing && x.EndsAtUtc <= now)) await CloseAsync(expired, now, cancellationToken);
        all = await database.GuildSeasons.Find(x => x.GuildId == guildId).SortBy(x => x.Sequence).ToListAsync(cancellationToken);
        var candidate = all.FirstOrDefault(x => x.Status == SeasonStatus.Scheduled && x.StartsAtUtc <= now && now < x.EndsAtUtc);
        if (candidate != null) await ActivateAsync(candidate, now, cancellationToken);
        // Future seasons are deliberately created by an explicit administrator action.
        // The coordinator only transitions persisted instances and never changes the plan.
    }

    private async Task<bool> TryAcquireLeaseAsync(ulong guildId, DateTime now, CancellationToken cancellationToken)
    {
        var filter = Builders<SeasonCoordinatorLease>.Filter.Eq(x => x.GuildId, guildId) &
            (Builders<SeasonCoordinatorLease>.Filter.Lt(x => x.ExpiresAtUtc, now) | Builders<SeasonCoordinatorLease>.Filter.Eq(x => x.OwnerId, instanceId));
        var update = Builders<SeasonCoordinatorLease>.Update.Set(x => x.OwnerId, instanceId).Set(x => x.ExpiresAtUtc, now.AddMinutes(2));
        var result = await database.SeasonCoordinatorLeases.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
        if (result.MatchedCount > 0) return true;

        try
        {
            await database.SeasonCoordinatorLeases.InsertOneAsync(new SeasonCoordinatorLease
            {
                GuildId = guildId,
                OwnerId = instanceId,
                ExpiresAtUtc = now.AddMinutes(2)
            }, cancellationToken: cancellationToken);
            return true;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another instance acquired or renewed the lease after our compare-and-set update.
            return false;
        }
    }

    private async Task PrepareAsync(GuildSeasonSettings settings, IReadOnlyList<GuildSeason> existing, CancellationToken cancellationToken)
    {
        if (settings.ScheduleKind == SeasonScheduleKind.Manual) return;
        var scheduled = existing.Count(x => x.Status == SeasonStatus.Scheduled);
        var missing = Math.Max(3, settings.PreparedSeasonCount) - scheduled;
        if (missing <= 0) return;
        var firstSequence = existing.Count == 0 ? 1 : existing.Max(x => x.Sequence) + 1;
        var generator = new SeasonScheduleGenerator();
        var generated = generator.Generate(settings, "Guild", firstSequence, missing);
        foreach (var item in generated)
        {
            var season = new GuildSeason { GuildId = settings.GuildId, Sequence = item.Sequence, Name = item.Name, StartsAtUtc = item.StartsAtUtc, EndsAtUtc = item.EndsAtUtc, CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime, Status = SeasonStatus.Scheduled, ScheduleRevision = settings.Revision, SettingsSnapshot = settings, PreviousSeasonId = existing.OrderByDescending(x => x.Sequence).FirstOrDefault()?.Id };
            try { await database.GuildSeasons.InsertOneAsync(season, cancellationToken: cancellationToken); }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        }
    }

    private async Task ActivateAsync(GuildSeason season, DateTime now, CancellationToken cancellationToken)
    {
        var update = Builders<GuildSeason>.Update.Set(x => x.Status, SeasonStatus.Active).Set(x => x.ActiveGuildId, season.GuildId).Set(x => x.ActivatedAtUtc, now);
        try
        {
            var result = await database.GuildSeasons.UpdateOneAsync(x => x.Id == season.Id && x.Status == SeasonStatus.Scheduled, update, cancellationToken: cancellationToken);
            if (result.ModifiedCount == 0) return;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { return; }
        await ApplyCarryOverAsync(season, now, cancellationToken);
        await reports.WriteAsync(new(season.GuildId, ReportCategories.Activity, ReportNames.SeasonStarted, ReportOutcomes.Succeeded, Metadata: new Dictionary<string, object?> { ["seasonId"] = season.Id, ["sequence"] = season.Sequence }), cancellationToken);
        await realtime.PublishGuildAsync(season.GuildId, cancellationToken);
    }

    private async Task ApplyCarryOverAsync(GuildSeason season, DateTime now, CancellationToken cancellationToken)
    {
        if (season.PreviousSeasonId == null || season.SettingsSnapshot.CarryOverMode == SeasonCarryOverMode.None) return;
        var latest = await database.GuildSeasons.Find(x => x.Id == season.Id).FirstOrDefaultAsync(cancellationToken);
        if (latest?.CarryOverApplied == true) return;
        var standings = await database.SeasonFinalStandings.Find(x => x.SeasonId == season.PreviousSeasonId).ToListAsync(cancellationToken);
        foreach (var standing in standings)
        {
            var value = decimal.Round(standing.TotalXp * season.SettingsSnapshot.CarryOverPercentage / 100m, 2, MidpointRounding.AwayFromZero);
            if (season.SettingsSnapshot.CarryOverMaximumXp is decimal maximum) value = Math.Min(value, maximum);
            var update = Builders<SeasonMemberXp>.Update.SetOnInsert(x => x.GuildId, season.GuildId).SetOnInsert(x => x.SeasonId, season.Id!).SetOnInsert(x => x.UserId, standing.UserId).SetOnInsert(x => x.DisplayName, standing.DisplayName).SetOnInsert(x => x.StartingXp, value).SetOnInsert(x => x.TotalXp, value).SetOnInsert(x => x.UpdatedAtUtc, now);
            await database.SeasonMemberXp.UpdateOneAsync(x => x.SeasonId == season.Id && x.UserId == standing.UserId, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
        }
        await database.GuildSeasons.UpdateOneAsync(x => x.Id == season.Id && !x.CarryOverApplied, Builders<GuildSeason>.Update.Set(x => x.CarryOverApplied, true), cancellationToken: cancellationToken);
        await reports.WriteAsync(new(season.GuildId, ReportCategories.Activity, ReportNames.SeasonCarryOverApplied, ReportOutcomes.Succeeded, Metadata: new Dictionary<string, object?> { ["seasonId"] = season.Id }), cancellationToken);
    }

    private async Task CloseAsync(GuildSeason season, DateTime now, CancellationToken cancellationToken)
    {
        await database.GuildSeasons.UpdateOneAsync(x => x.Id == season.Id && x.Status == SeasonStatus.Active, Builders<GuildSeason>.Update.Set(x => x.Status, SeasonStatus.Closing), cancellationToken: cancellationToken);
        var current = await database.GuildSeasons.Find(x => x.Id == season.Id).FirstOrDefaultAsync(cancellationToken);
        if (current == null || current.Finalized) return;
        if (current.RequiresFinalStandingRefresh)
            await database.SeasonFinalStandings.DeleteManyAsync(x => x.SeasonId == season.Id, cancellationToken);
        var members = await database.SeasonMemberXp.Find(x => x.SeasonId == season.Id).SortByDescending(x => x.TotalXp).ThenBy(x => x.UserId).ToListAsync(cancellationToken);
        var writes = members.Select((member, index) => new UpdateOneModel<SeasonFinalStanding>(Builders<SeasonFinalStanding>.Filter.Eq(x => x.SeasonId, season.Id) & Builders<SeasonFinalStanding>.Filter.Eq(x => x.UserId, member.UserId),
            Builders<SeasonFinalStanding>.Update.SetOnInsert(x => x.SeasonId, season.Id!).SetOnInsert(x => x.GuildId, season.GuildId).SetOnInsert(x => x.UserId, member.UserId).SetOnInsert(x => x.DisplayName, member.DisplayName).SetOnInsert(x => x.Rank, index + 1).SetOnInsert(x => x.TotalXp, member.TotalXp).SetOnInsert(x => x.Level, Mee6LevelCurve.GetLevel(member.TotalXp)).SetOnInsert(x => x.MessageCount, member.MessageCount).SetOnInsert(x => x.VoiceSeconds, member.VoiceSeconds).SetOnInsert(x => x.PublicLeaderboardVisible, member.PublicLeaderboardVisible).SetOnInsert(x => x.FinalizedAtUtc, now)) { IsUpsert = true }).ToList();
        if (writes.Count > 0) await database.SeasonFinalStandings.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken);
        var finalized = await database.GuildSeasons.UpdateOneAsync(x => x.Id == season.Id && !x.Finalized, Builders<GuildSeason>.Update.Set(x => x.Finalized, true).Set(x => x.RequiresFinalStandingRefresh, false).Set(x => x.Status, SeasonStatus.Closed).Unset(x => x.ActiveGuildId).Set(x => x.ClosedAtUtc, now), cancellationToken: cancellationToken);
        if (finalized.ModifiedCount > 0)
        {
            await reports.WriteAsync(new(season.GuildId, ReportCategories.Activity, ReportNames.SeasonClosed, ReportOutcomes.Succeeded, Metadata: new Dictionary<string, object?> { ["seasonId"] = season.Id }), cancellationToken);
            await realtime.PublishGuildAsync(season.GuildId, cancellationToken);
        }
    }
}
