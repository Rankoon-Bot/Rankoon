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
                var pending = await database.XpLedger.Find(x => x.ProjectionStatus == SeasonProjectionStatus.Pending).SortBy(x => x.CreatedAt).Limit(100).ToListAsync(stoppingToken);
                foreach (var ledger in pending)
                {
                    await xp.ProjectAsync(ledger, stoppingToken);
                    await reports.WriteAsync(new(ledger.GuildId, ReportCategories.Activity, ReportNames.XpProjectionRepaired, ReportOutcomes.Succeeded, SubjectId: ledger.UserId,
                        Metadata: new Dictionary<string, object?> { ["grantKey"] = ledger.GrantKey, ["seasonId"] = ledger.SeasonId }), stoppingToken);
                }
                health.Report("xp-projection-repair", WorkerHealthState.Healthy);
                await Task.Delay(pending.Count == 0 ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError(exception, "XP ledger projection repair failed");
                health.Report("xp-projection-repair", WorkerHealthState.Degraded, exception.GetType().Name);
                await errors.RecordAsync(new(exception, "worker", "xp.projection-repair", Worker: "xp-projection-repair"), stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
            }
        }
    }
}
