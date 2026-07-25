using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Operations;

namespace Rankoon.Data.Xp;

public sealed class VoiceActivityProjectionRepairService(RankoonDbContext database, IVoiceActivityProjectionService projection, IOperationalErrorRecorder errors, IWorkerHealthRegistry health, TimeProvider timeProvider, IOptions<VoiceActivityOptions> configuredOptions, ILogger<VoiceActivityProjectionRepairService> logger) : BackgroundService
{
    private readonly VoiceActivityOptions options = configuredOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;
                var due = now.AddSeconds(-options.ProjectionIntervalSeconds);
                var filter = (Builders<VoiceActivityDay>.Filter.Eq(x => x.ProjectionStatus, VoiceActivityProjectionStatus.Pending) & Builders<VoiceActivityDay>.Filter.Lte(x => x.UpdatedAtUtc, due)) |
                    (Builders<VoiceActivityDay>.Filter.Eq(x => x.ProjectionStatus, VoiceActivityProjectionStatus.Projecting) & Builders<VoiceActivityDay>.Filter.Lte(x => x.ProjectionLeaseExpiresAtUtc, now));
                var days = await database.VoiceActivities.Find(filter).SortBy(x => x.UpdatedAtUtc).Limit(options.ProjectionBatchSize).ToListAsync(stoppingToken);
                foreach (var day in days) await projection.ProjectAsync(day, null, stoppingToken);
                health.Report("voice-activity-projection", WorkerHealthState.Healthy);
                await Task.Delay(NextDelay(days.Count, options.ProjectionBatchSize, options.ProjectionIntervalSeconds), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Voice activity projection repair failed");
                health.Report("voice-activity-projection", WorkerHealthState.Degraded, exception.GetType().Name);
                try { await errors.RecordAsync(new(exception, "worker", "xp.voice-activity-projection", Worker: "voice-activity-projection"), stoppingToken); }
                catch (Exception recordingException) when (recordingException is not OperationCanceledException) { logger.LogWarning(recordingException, "Could not record voice projection repair failure"); }
                await Task.Delay(TimeSpan.FromSeconds(options.ProjectionIntervalSeconds), timeProvider, stoppingToken);
            }
        }
    }

    internal static TimeSpan NextDelay(int count, int batchSize, int intervalSeconds) => count >= batchSize
        ? TimeSpan.FromMilliseconds(250)
        : count > 0 ? TimeSpan.FromSeconds(Math.Max(1, intervalSeconds / 4)) : TimeSpan.FromSeconds(intervalSeconds);
}
