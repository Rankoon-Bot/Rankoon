using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;
using Rankoon.Data.Operations;

namespace Rankoon.Data.Xp;

/// <summary>Repairs ledger entries left pending by a process interruption between MongoDB writes.</summary>
public sealed class LedgerProjectionRepairService(RankoonDbContext database, XpService xp, IReportWriter reports, IOperationalErrorRecorder errors, IWorkerHealthRegistry health, TimeProvider timeProvider, ILogger<LedgerProjectionRepairService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;
                var filter = Builders<XpLedgerEntry>.Filter.Eq(x => x.ProjectionStatus, SeasonProjectionStatus.Pending) &
                    (Builders<XpLedgerEntry>.Filter.Eq(x => x.ProjectionLeaseOwner, null) | Builders<XpLedgerEntry>.Filter.Lte(x => x.ProjectionLeaseExpiresAtUtc, now));
                var pending = await database.XpLedger.Find(filter).SortBy(x => x.CreatedAt).Limit(100).ToListAsync(stoppingToken);
                var failures = await XpBatchProcessor.RunAsync(pending, async ledger =>
                {
                    await xp.ProjectAsync(ledger, stoppingToken);
                    var persisted = await database.XpLedger.Find(x => x.Id == ledger.Id).FirstOrDefaultAsync(stoppingToken);
                    if (persisted?.ProjectionStatus == SeasonProjectionStatus.Applied && !persisted.CooldownDenied)
                        await reports.WriteAsync(new(ledger.GuildId, ReportCategories.Activity, ReportNames.XpProjectionRepaired, ReportOutcomes.Succeeded, SubjectId: ledger.UserId,
                        Metadata: new Dictionary<string, object?> { ["grantKey"] = ledger.GrantKey, ["seasonId"] = ledger.SeasonId }), stoppingToken);
                }, stoppingToken);
                foreach (var failure in failures)
                {
                    logger.LogError(failure.Error, "XP ledger repair failed for {GrantKey}; other entries continue", failure.Item.GrantKey);
                    try { await errors.RecordAsync(new(failure.Error, "worker", "xp.projection-repair.entry", GuildId: failure.Item.GuildId, ActorUserId: failure.Item.UserId, Worker: "xp-projection-repair"), stoppingToken); }
                    catch (Exception recordingException) when (recordingException is not OperationCanceledException) { logger.LogWarning(recordingException, "Could not record ledger repair failure"); }
                }
                health.Report("xp-projection-repair", failures.Count == 0 ? WorkerHealthState.Healthy : WorkerHealthState.Degraded);
                await Task.Delay(pending.Count == 0 ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError(exception, "XP ledger projection repair failed");
                health.Report("xp-projection-repair", WorkerHealthState.Degraded, exception.GetType().Name);
                try { await errors.RecordAsync(new(exception, "worker", "xp.projection-repair", Worker: "xp-projection-repair"), stoppingToken); }
                catch (Exception recordingException) when (recordingException is not OperationCanceledException) { logger.LogWarning(recordingException, "Could not record ledger repair failure"); }
                await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
            }
        }
    }

    internal static bool IsEligibleForRepair(XpLedgerEntry entry, DateTime nowUtc) => entry.ProjectionStatus == SeasonProjectionStatus.Pending &&
        (entry.ProjectionLeaseOwner == null || entry.ProjectionLeaseExpiresAtUtc <= nowUtc);
}
