using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;

namespace Rankoon.Data.Xp;

public sealed record SeasonCoordinatorStatus(DateTimeOffset? LastRunAt, string? LastError, int EnabledGuildCount = 0, int LeasesHeld = 0);

public sealed class SeasonCoordinator(RankoonDbContext database, ISeasonLifecycleService lifecycle, TimeProvider timeProvider, ILogger<SeasonCoordinator> logger) : BackgroundService
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
                var run = await RunOnceAsync(stoppingToken);
                status = new(timeProvider.GetUtcNow(), null, run.EnabledGuildCount, run.LeasesHeld);
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

    public async Task<SeasonCoordinatorStatus> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var enabledGuilds = await database.GuildSeasonSettings.Find(x => x.Enabled).Project(x => x.GuildId).ToListAsync(cancellationToken);
        var leasesHeld = 0;
        foreach (var guildId in enabledGuilds) if (await RunGuildAsync(guildId, cancellationToken)) leasesHeld++;
        return new(timeProvider.GetUtcNow(), null, enabledGuilds.Count, leasesHeld);
    }

    private async Task<bool> RunGuildAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!await TryAcquireLeaseAsync(guildId, now, cancellationToken)) return false;
        var all = await database.GuildSeasons.Find(x => x.GuildId == guildId).SortBy(x => x.Sequence).ToListAsync(cancellationToken);
        foreach (var expired in all.Where(x => x.Status is SeasonStatus.Active or SeasonStatus.Closing && x.EndsAtUtc <= now)) await lifecycle.CloseAsync(guildId, expired.Id!, cancellationToken);
        all = await database.GuildSeasons.Find(x => x.GuildId == guildId).SortBy(x => x.Sequence).ToListAsync(cancellationToken);
        var candidate = all.FirstOrDefault(x => x.Status == SeasonStatus.Scheduled && x.StartsAtUtc <= now && now < x.EndsAtUtc);
        if (candidate != null) await lifecycle.ActivateAsync(guildId, candidate.Id!, cancellationToken);
        all = await database.GuildSeasons.Find(x => x.GuildId == guildId).SortBy(x => x.Sequence).ToListAsync(cancellationToken);
        var settings = await database.GuildSeasonSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (settings != null) await PrepareAsync(settings, all, cancellationToken);
        return true;
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
        var missing = settings.PreparedSeasonCount - scheduled;
        if (missing <= 0) return;
        var firstSequence = existing.Count == 0 ? 1 : existing.Max(x => x.Sequence) + 1;
        var generator = new SeasonScheduleGenerator();
        var generated = generator.Generate(settings, "Guild", 1, checked((int)(firstSequence - 1 + missing))).TakeLast(missing).ToList();
        var previousSeasonId = existing.OrderByDescending(x => x.Sequence).FirstOrDefault()?.Id;
        foreach (var item in generated)
        {
            if (existing.Any(current => current.StartsAtUtc < item.EndsAtUtc && item.StartsAtUtc < current.EndsAtUtc)) continue;
            var season = new GuildSeason { GuildId = settings.GuildId, Sequence = item.Sequence, Name = item.Name, StartsAtUtc = item.StartsAtUtc, EndsAtUtc = item.EndsAtUtc, CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime, Status = SeasonStatus.Scheduled, ScheduleRevision = settings.Revision, SettingsSnapshot = settings, PreviousSeasonId = previousSeasonId };
            try { await database.GuildSeasons.InsertOneAsync(season, cancellationToken: cancellationToken); previousSeasonId = season.Id; existing = existing.Append(season).ToList(); }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        }
    }
}
