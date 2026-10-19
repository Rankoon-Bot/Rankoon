using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Controllers;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Operations;

namespace Rankoon.Data.Xp;

internal sealed record XpIntegrityTotals(decimal EarnedXp, decimal ManualXp, decimal VoiceXp, decimal SilentMigrationXp = 0);
internal sealed record XpIntegrityMemberResult(int Corrected, int Queued, bool Deferred = false, bool NeedsReview = false);
internal sealed record XpIntegrityRunResult(int Checked, int Corrected, int Queued, int Deferred, int Failed);
internal sealed record XpIntegrityGuild(ulong GuildId);

/// <summary>Independent, bounded verification of durable XP and owed level delivery.</summary>
public sealed class XpIntegrityService(RankoonDbContext database, IXpProjectionCoordinator coordinator, ILevelTransitionService transitions,
    ILeaderboardRealtimePublisher realtime, IOperationalErrorRecorder errors, IWorkerHealthRegistry health,
    TimeProvider timeProvider, IOptions<XpIntegrityOptions> configuredOptions, ILogger<XpIntegrityService> logger) : BackgroundService
{
    private readonly XpIntegrityOptions options = configuredOptions.Value;
    private readonly SemaphoreSlim runGate = new(1, 1);
    internal const string WorkerName = "xp-integrity";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) { health.Report(WorkerName, WorkerHealthState.Healthy, "disabled"); return; }
        var nextRun = DateTimeOffset.MinValue;
        var state = WorkerHealthState.Healthy;
        var detail = "starting";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (timeProvider.GetUtcNow() >= nextRun)
                {
                    var result = await RunOnceAsync(stoppingToken);
                    state = result.Failed == 0 ? WorkerHealthState.Healthy : WorkerHealthState.Degraded;
                    detail = $"checked={result.Checked}; corrected={result.Corrected}; queued={result.Queued}; deferred={result.Deferred}; failed={result.Failed}";
                    nextRun = timeProvider.GetUtcNow().AddMinutes(options.IntervalMinutes);
                    logger.LogInformation("XP integrity check: {Detail}", detail);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                state = WorkerHealthState.Degraded; detail = exception.GetType().Name;
                logger.LogError(exception, "XP integrity sweep failed; retrying in five minutes");
                await RecordFailureAsync(exception, null, null, stoppingToken);
                nextRun = timeProvider.GetUtcNow().AddMinutes(5);
            }
            // Keep health fresh between hourly sweeps; idle time is not a stalled worker.
            health.Report(WorkerName, state, detail);
            await Task.Delay(TimeSpan.FromMinutes(1), timeProvider, stoppingToken);
        }
    }

    internal async Task<XpIntegrityRunResult> RunOnceAsync(CancellationToken ct = default)
    {
        if (!options.Enabled || !await runGate.WaitAsync(0, ct)) return new(0, 0, 0, 0, 0);
        try
        {
            var checkedCount = 0; var corrected = 0; var queued = 0; var deferred = 0; var failed = 0;
            ulong afterGuild = 0;
            while (true)
            {
                var guilds = await database.MemberXp.Aggregate().Match(x => x.GuildId > afterGuild && x.IsCurrentMember && !x.IsDevelopmentMock)
                    .Group(x => x.GuildId, group => new XpIntegrityGuild(group.Key)).SortBy(x => x.GuildId).Limit(options.BatchSize).ToListAsync(ct);
                if (guilds.Count == 0) break;
                foreach (var guild in guilds)
                {
                    var cursorKey = $"guild:{guild.GuildId}";
                    var cursor = await database.XpIntegritySweepCursors.Find(x => x.Id == cursorKey).FirstOrDefaultAsync(ct);
                    var initialUser = cursor?.AfterUserId ?? 0;
                    var afterUser = initialUser;
                    var wrapped = false;
                    var budget = options.MaxAnnouncementsPerGuildPerRun;
                    while (true)
                    {
                        var memberFilter = Builders<MemberXp>.Filter.Where(x => x.GuildId == guild.GuildId && x.IsCurrentMember && !x.IsDevelopmentMock && x.UserId > afterUser);
                        if (wrapped) memberFilter &= Builders<MemberXp>.Filter.Lte(x => x.UserId, initialUser);
                        var members = await database.MemberXp.Find(memberFilter)
                            .SortBy(x => x.UserId).Limit(options.BatchSize).ToListAsync(ct);
                        if (members.Count == 0)
                        {
                            if (initialUser > 0 && !wrapped) { wrapped = true; afterUser = 0; continue; }
                            break;
                        }
                        foreach (var member in members)
                        {
                            health.Report(WorkerName, failed == 0 ? WorkerHealthState.Healthy : WorkerHealthState.Degraded, $"scanning; checked={checkedCount}; failed={failed}");
                            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            attempt.CancelAfter(TimeSpan.FromSeconds(options.MemberTimeoutSeconds));
                            try
                            {
                                var result = await CheckMemberAsync(guild.GuildId, member.UserId, budget, attempt.Token);
                                checkedCount++; corrected += result.Corrected; queued += result.Queued; budget -= result.Queued;
                                if (result.Queued > 0 && budget == 0)
                                    await database.XpIntegritySweepCursors.UpdateOneAsync(x => x.Id == cursorKey,
                                        Builders<XpIntegritySweepCursor>.Update.Set(x => x.AfterUserId, member.UserId).Set(x => x.UpdatedAtUtc, timeProvider.GetUtcNow().UtcDateTime),
                                        new UpdateOptions { IsUpsert = true }, ct);
                                if (result.Deferred) deferred++;
                                if (result.NeedsReview) failed++;
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch (Exception exception)
                            {
                                failed++;
                                logger.LogWarning(exception, "XP integrity check failed for {GuildId}/{UserId}; other members continue", guild.GuildId, member.UserId);
                                await RecordFailureAsync(exception, guild.GuildId, member.UserId, ct);
                            }
                        }
                        afterUser = members[^1].UserId;
                        health.Report(WorkerName, failed == 0 ? WorkerHealthState.Healthy : WorkerHealthState.Degraded, $"scanning; checked={checkedCount}; corrected={corrected}; queued={queued}; failed={failed}");
                        await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, ct);
                    }
                }
                afterGuild = guilds[^1].GuildId;
            }
            return new(checkedCount, corrected, queued, deferred, failed);
        }
        finally { runGate.Release(); }
    }

    internal async Task<XpIntegrityMemberResult> CheckMemberAsync(ulong guildId, ulong userId, int announcementBudget, CancellationToken ct = default)
    {
        await using var lease = await coordinator.AcquireAsync(guildId, userId, null, ct);
        if (lease == null) return new(0, 0, Deferred: true);
        var member = await database.MemberXp.Find(x => x.GuildId == guildId && x.UserId == userId).FirstOrDefaultAsync(ct);
        if (member == null || !member.IsCurrentMember || member.IsDevelopmentMock) return new(0, 0);
        // An interrupted projection must finish its own recovery before comparison.
        if (await database.XpLedger.Find(x => x.GuildId == guildId && x.UserId == userId && x.ProjectionStatus == SeasonProjectionStatus.Pending).AnyAsync(ct) ||
            await database.VoiceActivities.Find(x => x.GuildId == guildId && x.UserId == userId && x.ProjectionStatus == VoiceActivityProjectionStatus.Projecting).AnyAsync(ct))
            return new(0, 0, Deferred: true);

        var migration = await database.VoiceLedgerMigrationStates.Find(x => x.Id == VoiceLedgerMigrationState.SingletonId).FirstOrDefaultAsync(ct);
        var compressed = VoiceLedgerMigrationService.IsCompressedVoiceAuthoritative(migration);
        var days = await database.VoiceActivities.Find(x => x.GuildId == guildId && x.UserId == userId).ToListAsync(ct);
        var inconsistentDays = days.Where(x => x.ProjectionStatus == VoiceActivityProjectionStatus.Applied &&
            (x.ProjectionRevision != x.ProjectedRevision || x.TotalAwardedXp != x.ProjectedXp || x.TotalEligibleSeconds != x.ProjectedEligibleSeconds)).ToArray();
        if (inconsistentDays.Length > 0)
        {
            var marked = 0;
            foreach (var day in inconsistentDays)
            {
                if (day.TotalAwardedXp < day.ProjectedXp || day.TotalEligibleSeconds < day.ProjectedEligibleSeconds || day.ProjectionRevision < day.ProjectedRevision)
                {
                    await RecordFailureAsync(new InvalidOperationException($"Voice projection {day.Id} exceeds its source activity; inspect before reducing XP."), guildId, userId, ct);
                    return new(marked, 0, NeedsReview: true);
                }
                if (!await lease.RenewAsync(ct)) return new(marked, 0, Deferred: true);
                var result = await database.VoiceActivities.UpdateOneAsync(x => x.Id == day.Id && x.Revision == day.Revision && x.ProjectionStatus == VoiceActivityProjectionStatus.Applied,
                    Builders<VoiceActivityDay>.Update.Set(x => x.ProjectionStatus, VoiceActivityProjectionStatus.Pending).Inc(x => x.Revision, 1), cancellationToken: ct);
                if (result.ModifiedCount == 1) marked++;
            }
            // Let the normal idempotent voice pipeline project these durable awards.
            return new(marked, 0, Deferred: true);
        }
        var voice = VoiceActivityProjectionService.Snapshot(days, new VoiceActivityDay(), compressed);
        var totals = await LedgerTotalsAsync(guildId, userId, null, compressed, ct);
        totals = totals with { EarnedXp = totals.EarnedXp + voice.AwardedXp, VoiceXp = totals.VoiceXp + voice.AwardedXp };
        var needsReview = WouldReduceSources(member.EarnedXp, member.ManualAdjustment, totals);
        var corrected = 0; var queued = 0;
        if (needsReview)
        {
            await RecordFailureAsync(new InvalidOperationException("XP source history is below the stored balance. Inspect ledger completeness before reducing XP."), guildId, userId, ct);
            logger.LogWarning("XP history for {GuildId}/{UserId} needs review; no XP or announcement changed", guildId, userId);
            return new(0, 0, NeedsReview: true);
        }
        var expected = member.ImportedMee6Xp + totals.EarnedXp + totals.ManualXp;
        if (member.TotalXp != expected || member.EarnedXp != totals.EarnedXp || member.ManualAdjustment != totals.ManualXp || member.VoiceXp != totals.VoiceXp)
        {
            if (!await lease.RenewAsync(ct)) return new(0, 0, Deferred: true);
            await database.MemberXp.UpdateOneAsync(x => x.Id == member.Id,
                Builders<MemberXp>.Update.Set(x => x.EarnedXp, totals.EarnedXp).Set(x => x.ManualAdjustment, totals.ManualXp)
                    .Set(x => x.VoiceXp, totals.VoiceXp).Set(x => x.TotalXp, expected).Set(x => x.UpdatedAt, timeProvider.GetUtcNow().UtcDateTime), cancellationToken: ct);
            corrected++;
            logger.LogInformation("XP integrity restored member {GuildId}/{UserId}: total {Before} -> {After}, earned {Earned}, manual {Manual}",
                guildId, userId, member.TotalXp, expected, totals.EarnedXp, totals.ManualXp);
        }

        var announcementSettings = await new XpSettingsStore(database, timeProvider).LoadAnnouncementsAsync(guildId, ct);
        var baseline = member.ImportedMee6Xp + (announcementSettings?.Lifetime.AnnounceManualAdjustments == true ? totals.SilentMigrationXp : totals.ManualXp);
        queued += await EnsureDeliveryAsync(guildId, userId, expected, baseline, LevelProgressScope.Lifetime, null, null,
            announcementSettings?.Lifetime, announcementBudget, lease, ct);

        var seasons = await database.GuildSeasons.Find(x => x.GuildId == guildId && x.Status == SeasonStatus.Active).ToListAsync(ct);
        foreach (var season in seasons)
        {
            var seasonMember = await database.SeasonMemberXp.Find(x => x.SeasonId == season.Id && x.UserId == userId).FirstOrDefaultAsync(ct);
            var seasonTotals = await LedgerTotalsAsync(guildId, userId, season.Id, compressed, ct);
            voice.Seasons.TryGetValue(season.Id!, out var seasonVoice);
            seasonTotals = seasonTotals with { EarnedXp = seasonTotals.EarnedXp + (seasonVoice?.AwardedXp ?? 0), VoiceXp = seasonTotals.VoiceXp + (seasonVoice?.AwardedXp ?? 0) };
            if (seasonMember == null && seasonTotals == new XpIntegrityTotals(0, 0, 0)) continue;
            if (WouldReduceSources(seasonMember?.EarnedXp ?? 0, seasonMember?.ManualAdjustment ?? 0, seasonTotals))
            {
                needsReview = true;
                await RecordFailureAsync(new InvalidOperationException($"Season XP history is incomplete for {season.Id}; inspect before reducing XP."), guildId, userId, ct);
                continue;
            }
            var starting = seasonMember?.StartingXp ?? 0;
            var total = starting + seasonTotals.EarnedXp + seasonTotals.ManualXp;
            if (seasonMember == null || seasonMember.TotalXp != total || seasonMember.EarnedXp != seasonTotals.EarnedXp || seasonMember.ManualAdjustment != seasonTotals.ManualXp || seasonMember.VoiceXp != seasonTotals.VoiceXp)
            {
                if (!await lease.RenewAsync(ct)) return new(corrected, queued, Deferred: true, NeedsReview: needsReview);
                await database.SeasonMemberXp.UpdateOneAsync(x => x.SeasonId == season.Id && x.UserId == userId,
                    Builders<SeasonMemberXp>.Update.SetOnInsert(x => x.GuildId, guildId).SetOnInsert(x => x.UserId, userId).SetOnInsert(x => x.SeasonId, season.Id!)
                        .SetOnInsert(x => x.StartingXp, starting).SetOnInsert(x => x.DisplayName, member.DisplayName).SetOnInsert(x => x.IsCurrentMember, true)
                        .SetOnInsert(x => x.PublicLeaderboardVisible, member.PublicLeaderboardVisible)
                        .Set(x => x.EarnedXp, seasonTotals.EarnedXp).Set(x => x.ManualAdjustment, seasonTotals.ManualXp).Set(x => x.VoiceXp, seasonTotals.VoiceXp)
                        .Set(x => x.TotalXp, total).Set(x => x.UpdatedAtUtc, timeProvider.GetUtcNow().UtcDateTime), new UpdateOptions { IsUpsert = true }, ct);
                corrected++;
                logger.LogInformation("XP integrity restored season {SeasonId} member {GuildId}/{UserId}: total {Before} -> {After}",
                    season.Id, guildId, userId, seasonMember?.TotalXp ?? 0, total);
            }
            var seasonBaseline = starting + (announcementSettings?.Season.AnnounceManualAdjustments == true ? seasonTotals.SilentMigrationXp : seasonTotals.ManualXp);
            queued += await EnsureDeliveryAsync(guildId, userId, total, seasonBaseline, LevelProgressScope.Season, season.Id, season.Name,
                announcementSettings?.Season, announcementBudget - queued, lease, ct);
        }
        if (corrected > 0) await realtime.PublishMemberAsync(guildId, userId, ct);
        var unresolvedDelivery = await database.LevelTransitionEvents.Find(x => x.GuildId == guildId && x.UserId == userId &&
            (x.Status == LevelTransitionStatus.DeadLetter || x.Status == LevelTransitionStatus.RetryScheduled && x.DeliveryAttempts >= 5)).AnyAsync(ct);
        return new(corrected, queued, NeedsReview: needsReview || unresolvedDelivery);
    }

    private async Task<int> EnsureDeliveryAsync(ulong guildId, ulong userId, decimal total, decimal baseline, LevelProgressScope scope, string? seasonId, string? seasonName,
        LevelAnnouncementProfile? profile, int budget, IXpProjectionLease lease, CancellationToken ct)
    {
        if (!options.CatchUpAnnouncements || budget <= 0) return 0;
        var level = Mee6LevelCurve.GetLevel(total);
        var filter = Builders<LevelTransitionEvent>.Filter.Eq(x => x.GuildId, guildId) & Builders<LevelTransitionEvent>.Filter.Eq(x => x.UserId, userId) &
            Builders<LevelTransitionEvent>.Filter.Eq(x => x.Scope, scope) & Builders<LevelTransitionEvent>.Filter.Eq(x => x.SeasonId, seasonId);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var retry = await database.LevelTransitionEvents.Find(filter & Builders<LevelTransitionEvent>.Filter.Eq(x => x.Status, LevelTransitionStatus.DeadLetter) &
            Builders<LevelTransitionEvent>.Filter.Eq(x => x.DiscordMessageId, null) & Builders<LevelTransitionEvent>.Filter.Lte(x => x.NewLevel, level) &
            (Builders<LevelTransitionEvent>.Filter.Eq(x => x.LastIntegrityRetryAtUtc, null) | Builders<LevelTransitionEvent>.Filter.Lte(x => x.LastIntegrityRetryAtUtc, now.AddMinutes(-options.IntervalMinutes))))
            .SortByDescending(x => x.NewLevel).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (retry != null)
        {
            if (!await lease.RenewAsync(ct)) return 0;
            if (await database.LevelTransitionEvents.Find(filter & Builders<LevelTransitionEvent>.Filter.Eq(x => x.Status, LevelTransitionStatus.Delivered) &
                Builders<LevelTransitionEvent>.Filter.Gte(x => x.NewLevel, retry.NewLevel) & Builders<LevelTransitionEvent>.Filter.Gte(x => x.CreatedAtUtc, retry.CreatedAtUtc)).AnyAsync(ct))
            {
                await database.LevelTransitionEvents.UpdateOneAsync(x => x.Id == retry.Id && x.Status == LevelTransitionStatus.DeadLetter,
                    Builders<LevelTransitionEvent>.Update.Set(x => x.Status, LevelTransitionStatus.CompletedWithoutAnnouncement)
                        .Set(x => x.LastErrorCode, "supersededByDeliveredLevel").Set(x => x.CompletedAtUtc, now), cancellationToken: ct);
                return 0;
            }
            var result = await database.LevelTransitionEvents.UpdateOneAsync(x => x.Id == retry.Id && x.Status == LevelTransitionStatus.DeadLetter && x.DiscordMessageId == null,
                Builders<LevelTransitionEvent>.Update.Set(x => x.Status, LevelTransitionStatus.RetryScheduled).Set(x => x.NextAttemptAtUtc, now)
                    .Set(x => x.CompletedAtUtc, null).Set(x => x.LastIntegrityRetryAtUtc, now).Unset(x => x.LeaseOwner).Unset(x => x.LeaseExpiresAtUtc), cancellationToken: ct);
            if (result.ModifiedCount == 1) logger.LogInformation("XP integrity requeued failed delivery {EventKey} for {GuildId}/{UserId}", retry.EventKey, guildId, userId);
            return result.ModifiedCount == 1 ? 1 : 0;
        }
        if (profile?.Enabled != true || !profile.ChannelId.HasValue) return 0;
        var highest = await database.LevelTransitionEvents.Find(filter).SortByDescending(x => x.NewLevel).Project(x => (int?)x.NewLevel).FirstOrDefaultAsync(ct);
        var fromLevel = Math.Max(Mee6LevelCurve.GetLevel(baseline), highest ?? 0);
        if (level <= fromLevel) return 0;
        if (!await lease.RenewAsync(ct)) return 0;
        var previous = Math.Max(baseline, fromLevel == 0 ? 0 : Mee6LevelCurve.RequiredXpForLevel(fromLevel));
        var key = CatchUpKey(guildId, userId, scope, seasonId, level);
        await transitions.EnsureAsync(guildId, userId, new("xp_reconciliation", key, total - previous), XpService.TransitionSnapshot(previous, total), scope, seasonId, seasonName, ct);
        logger.LogInformation("XP integrity queued catch-up {EventKey} for {GuildId}/{UserId}: level {Before} -> {After}", key, guildId, userId, fromLevel, level);
        return 1;
    }

    internal static string CatchUpKey(ulong guildId, ulong userId, LevelProgressScope scope, string? seasonId, int level) =>
        $"xp-integrity:{guildId}:{userId}:{scope}:{seasonId ?? "lifetime"}:{level}";
    internal static bool WouldReduceSources(decimal earned, decimal manual, XpIntegrityTotals expected) => expected.EarnedXp < earned || expected.ManualXp < manual;

    private async Task<XpIntegrityTotals> LedgerTotalsAsync(ulong guildId, ulong userId, string? seasonId, bool compressed, CancellationToken ct)
    {
        var filter = Builders<XpLedgerEntry>.Filter.Eq(x => x.GuildId, guildId) & Builders<XpLedgerEntry>.Filter.Eq(x => x.UserId, userId) &
            Builders<XpLedgerEntry>.Filter.Eq(x => x.ProjectionStatus, SeasonProjectionStatus.Applied) & Builders<XpLedgerEntry>.Filter.Ne(x => x.IsProjectionControl, true) & Builders<XpLedgerEntry>.Filter.Ne(x => x.CooldownDenied, true);
        filter &= seasonId == null ? Builders<XpLedgerEntry>.Filter.Ne(x => x.Scope, XpLedgerScope.SeasonOnly) :
            Builders<XpLedgerEntry>.Filter.Eq(x => x.SeasonId, seasonId) & Builders<XpLedgerEntry>.Filter.Ne(x => x.Scope, XpLedgerScope.LifetimeOnly);
        if (compressed) filter &= Builders<XpLedgerEntry>.Filter.Ne(x => x.Source, "voice");
        var automatic = new BsonDocument("$in", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$kind", "AutomaticGrant" }), new BsonArray { "AutomaticGrant", "AutomaticReversal" } });
        var voice = new BsonDocument("$and", new BsonArray { automatic, new BsonDocument("$in", new BsonArray { "$source", new BsonArray { "voice", "voice_reversal" } }) });
        var grouped = await database.XpLedger.Aggregate().Match(filter).Group<BsonDocument>(new BsonDocument
        {
            { "_id", BsonNull.Value },
            { "earned", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { automatic, "$amount", 0 })) },
            { "manual", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { automatic, 0, "$amount" })) },
            { "silent", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$kind", "SystemMigration" }), "$amount", 0 })) },
            { "voice", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { voice, "$amount", 0 })) }
        }).FirstOrDefaultAsync(ct);
        return grouped == null ? new(0, 0, 0) : new(grouped["earned"].ToDecimal(), grouped["manual"].ToDecimal(), grouped["voice"].ToDecimal(), grouped["silent"].ToDecimal());
    }

    private async Task RecordFailureAsync(Exception exception, ulong? guildId, ulong? userId, CancellationToken ct)
    {
        try { await errors.RecordAsync(new(exception, "worker", "xp.integrity", GuildId: guildId, ActorUserId: userId, Worker: WorkerName), ct); }
        catch (Exception recordingException) when (recordingException is not OperationCanceledException) { logger.LogWarning(recordingException, "Could not record XP integrity failure"); }
    }
}
