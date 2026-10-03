using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;
using Rankoon.Data.Operations;

namespace Rankoon.Data.Xp;

public sealed record SeasonCoordinatorStatus(DateTimeOffset? LastRunAt, string? LastError, int EnabledGuildCount = 0, int LeasesHeld = 0);

public sealed class SeasonCoordinator(RankoonDbContext database, ISeasonLifecycleService lifecycle, SeasonPlanningService planning, IOperationalErrorRecorder errors, IWorkerHealthRegistry health, TimeProvider timeProvider, ILogger<SeasonCoordinator> logger) : BackgroundService
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
                status = run;
                health.Report("season-coordinator", run.LastError == null ? WorkerHealthState.Healthy : WorkerHealthState.Degraded, run.LastError);
                await Task.Delay(TimeSpan.FromMinutes(1), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Season coordinator failed");
                status = new(timeProvider.GetUtcNow(), exception.GetBaseException().GetType().Name);
                health.Report("season-coordinator", WorkerHealthState.Degraded, exception.GetType().Name);
                await errors.RecordAsync(new(exception, "worker", "season.coordinator", Worker: "season-coordinator"), stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
            }
        }
    }

    public async Task<SeasonCoordinatorStatus> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var enabledGuilds = await database.GuildSeasonSettings.Find(x => x.Enabled).Project(x => x.GuildId).ToListAsync(cancellationToken);
        var pendingGuilds = await database.SeasonSetupOperations.Find(x => !x.Completed && x.Settings != null).Project(x => x.GuildId).ToListAsync(cancellationToken);
        enabledGuilds = enabledGuilds.Concat(pendingGuilds).Distinct().ToList();
        var leasesHeld = 0;
        string? lastError = null;
        foreach (var guildId in enabledGuilds)
        {
            try { if (await RunGuildAsync(guildId, cancellationToken)) leasesHeld++; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                lastError = exception.GetBaseException().GetType().Name;
                logger.LogError(exception, "Season coordinator failed for guild {GuildId}", guildId);
                await errors.RecordAsync(new(exception, "worker", "season.coordinator", Worker: "season-coordinator"), cancellationToken);
            }
        }
        return new(timeProvider.GetUtcNow(), lastError, enabledGuilds.Count, leasesHeld);
    }

    private async Task<bool> RunGuildAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!await TryAcquireLeaseAsync(guildId, now, cancellationToken)) return false;
        await using var lease = await planning.AcquireLeaseAsync(guildId, cancellationToken);
        cancellationToken = lease.CancellationToken;
        var settings = await database.GuildSeasonSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (settings?.Enabled != true) return false;
        var all = await database.GuildSeasons.Find(x => x.GuildId == guildId).SortBy(x => x.Sequence).ToListAsync(cancellationToken);
        foreach (var season in all.Where(x => x.Status == SeasonStatus.Closing || x.Status == SeasonStatus.Active && x.EndsAtUtc <= now || x.Status == SeasonStatus.Closed && (!x.CloseReported || !x.CloseRealtimePublished)))
            await lifecycle.CloseAsync(guildId, season.Id!, cancellationToken);
        // A restart must finish baseline/carry-over initialization before attempting a successor.
        foreach (var active in all.Where(x => x.Status == SeasonStatus.Active && x.EndsAtUtc > now && (!x.BaselineInitialized || x.PreviousSeasonId != null && x.SettingsSnapshot.CarryOverMode != SeasonCarryOverMode.None && !x.CarryOverApplied || !x.StartReported || !x.StartRealtimePublished)))
            await lifecycle.ActivateAsync(guildId, active.Id!, cancellationToken);
        // Downtime can leave scheduled periods entirely in the past. They never ran.
        await database.GuildSeasons.UpdateManyAsync(x => x.GuildId == guildId && x.Status == SeasonStatus.Scheduled && x.EndsAtUtc <= now,
            Builders<GuildSeason>.Update.Set(x => x.Status, SeasonStatus.Cancelled).Set(x => x.ClosedAtUtc, now), cancellationToken: cancellationToken);
        all = await database.GuildSeasons.Find(x => x.GuildId == guildId).SortBy(x => x.Sequence).ToListAsync(cancellationToken);
        await PrepareAsync(settings, all, now, cancellationToken);
        all = await database.GuildSeasons.Find(x => x.GuildId == guildId).SortBy(x => x.Sequence).ToListAsync(cancellationToken);
        var candidate = all.FirstOrDefault(x => x.Status == SeasonStatus.Scheduled && x.StartsAtUtc <= now && now < x.EndsAtUtc);
        if (candidate != null) await lifecycle.ActivateAsync(guildId, candidate.Id!, cancellationToken);
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

    private async Task PrepareAsync(GuildSeasonSettings settings, IReadOnlyList<GuildSeason> existing, DateTime now, CancellationToken cancellationToken)
    {
        if (settings.PlanningMode != SeasonPlanningMode.MaintainPreparedBuffer || settings.ScheduleKind == SeasonScheduleKind.Manual) return;
        var missing = settings.PreparedSeasonCount - CountPrepared(existing, now);
        if (missing <= 0) return;
        await planning.PlanUnderLeaseAsync(settings, missing, cancellationToken);
    }

    public static int CountPrepared(IEnumerable<GuildSeason> seasons, DateTime? notEndedAfterUtc = null) => seasons.Count(x =>
        x.Status is SeasonStatus.Scheduled or SeasonStatus.Active or SeasonStatus.Closing &&
        (!notEndedAfterUtc.HasValue || x.EndsAtUtc > notEndedAfterUtc.Value));
}
