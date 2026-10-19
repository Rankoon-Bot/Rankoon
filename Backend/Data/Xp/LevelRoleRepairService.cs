using MongoDB.Driver;
using Discord.Net;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Operations;

namespace Rankoon.Data.Xp;

/// <summary>Converges configured rewards even when an event, import or settings update missed them.</summary>
public sealed class LevelRoleRepairService(RankoonDbContext database, LevelRoleService roles, SeasonLevelRoleService seasonRoles,
    IWorkerHealthRegistry health, TimeProvider timeProvider, ILogger<LevelRoleRepairService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var failures = 0;
                var settings = await database.GuildXpSettings.Find(x => x.LevelRoles.Any()).ToListAsync(stoppingToken);
                var seasons = await database.GuildSeasons.Find(x => x.Status == SeasonStatus.Active && x.SettingsSnapshot.SeasonLevelRoles.Any()).ToListAsync(stoppingToken);
                foreach (var setting in settings)
                    failures += await RepairMembersAsync(setting.GuildId, null, stoppingToken);
                foreach (var season in seasons)
                    failures += await RepairMembersAsync(season.GuildId, season.Id, stoppingToken);
                health.Report("level-role-repair", failures == 0 ? WorkerHealthState.Healthy : WorkerHealthState.Degraded,
                    failures == 0 ? null : "roleSynchronizationFailed");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Level role repair failed; the next sweep will retry");
                health.Report("level-role-repair", WorkerHealthState.Degraded, exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromMinutes(10), timeProvider, stoppingToken);
        }
    }

    private async Task<int> RepairMembersAsync(ulong guildId, string? seasonId, CancellationToken ct)
    {
        ulong after = 0;
        var failures = 0;
        var roleFailureCounts = new Dictionary<(ulong RoleId, string ErrorCode), int>();
        var affectedUsers = new List<ulong>();

        void LogFailureSummary()
        {
            if (failures == 0) return;
            var details = string.Join(", ", roleFailureCounts.Select(x => $"{x.Key.RoleId}:{x.Key.ErrorCode}={x.Value}"));
            logger.LogWarning("Level role repair found {FailedMembers} affected members in guild {GuildId}: {RoleFailures}. Sample members: {UserIds}. Check role existence, Manage Roles permission and bot role hierarchy; the next sweep retries",
                failures, guildId, details, string.Join(", ", affectedUsers));
        }

        while (!ct.IsCancellationRequested)
        {
            var users = seasonId == null
                ? await database.MemberXp.Find(x => x.GuildId == guildId && x.IsCurrentMember && !x.IsDevelopmentMock && x.UserId > after).SortBy(x => x.UserId).Limit(100).Project(x => x.UserId).ToListAsync(ct)
                : await database.SeasonMemberXp.Find(x => x.SeasonId == seasonId && x.IsCurrentMember && !x.IsDevelopmentMock && x.UserId > after).SortBy(x => x.UserId).Limit(100).Project(x => x.UserId).ToListAsync(ct);
            if (users.Count == 0)
            {
                LogFailureSummary();
                return failures;
            }
            foreach (var userId in users)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    var result = seasonId == null
                        ? await roles.SynchronizeAsync(guildId, userId, attempt.Token)
                        : await seasonRoles.SynchronizeAsync(guildId, seasonId, userId, attempt.Token);
                    if (result.Failed.Count > 0)
                    {
                        failures++;
                        if (affectedUsers.Count < 5 && !affectedUsers.Contains(userId)) affectedUsers.Add(userId);
                        foreach (var failure in result.Failed)
                        {
                            var key = (failure.RoleId, failure.ErrorCode);
                            roleFailureCounts[key] = roleFailureCounts.GetValueOrDefault(key) + 1;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (DiscordGuildUnavailableException)
                {
                    LogFailureSummary();
                    logger.LogInformation("Level role repair deferred for guild {GuildId}; the bot is disconnected and the next sweep will retry", guildId);
                    return failures;
                }
                catch (OperationCanceledException) when (attempt.IsCancellationRequested)
                {
                    LogFailureSummary();
                    logger.LogWarning("Level role repair timed out for guild {GuildId} at member {UserId}; the next sweep will retry", guildId, userId);
                    return failures + 1;
                }
                catch (HttpException exception)
                {
                    LogFailureSummary();
                    logger.LogWarning("Level role repair stopped for guild {GuildId} at member {UserId}; Discord returned HTTP {HttpStatus} and code {DiscordCode}. The next sweep will retry",
                        guildId, userId, exception.HttpCode, exception.DiscordCode);
                    return failures + 1;
                }
                catch (Exception exception)
                {
                    LogFailureSummary();
                    logger.LogWarning(exception, "Level role repair for {GuildId}/{UserId} interrupted; the next sweep retries", guildId, userId);
                    // Stop this guild on infrastructure failures instead of issuing one failed request per member.
                    return failures + 1;
                }
            }
            after = users[^1];
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, ct);
        }
        return failures;
    }
}
