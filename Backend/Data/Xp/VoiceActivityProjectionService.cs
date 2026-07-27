using MongoDB.Driver;
using Microsoft.Extensions.Options;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Performance;

namespace Rankoon.Data.Xp;

public sealed record VoiceProjectionTotals(long EligibleSeconds, decimal AwardedXp, IReadOnlyDictionary<string, VoiceSeasonTotal> Seasons);

public interface IVoiceActivityProjectionService
{
    Task ProjectPendingAsync(ulong guildId, ulong userId, string? displayName, CancellationToken cancellationToken = default, bool immediate = false);
    Task ProjectAsync(VoiceActivityDay day, string? displayName, CancellationToken cancellationToken = default);
}

public sealed class VoiceActivityProjectionService(RankoonDbContext database, IXpProjectionCoordinator coordinator, ILevelTransitionService transitions, ILeaderboardRealtimePublisher realtime, TimeProvider timeProvider, IOptions<VoiceActivityOptions> configuredOptions) : IVoiceActivityProjectionService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private readonly VoiceActivityOptions options = configuredOptions.Value;
    private readonly TimeSpan projectionInterval = TimeSpan.FromSeconds(configuredOptions.Value.ProjectionIntervalSeconds);

    public async Task ProjectPendingAsync(ulong guildId, ulong userId, string? displayName, CancellationToken cancellationToken = default, bool immediate = false)
    {
        using var measurement = RankoonPerformanceMetrics.Start("xp.voice.projection.pending", "voice_activity_projection");
        measurement.AddDatabaseOperation();
        var days = await database.VoiceActivities.Find(x => x.GuildId == guildId && x.UserId == userId && x.ProjectionStatus != VoiceActivityProjectionStatus.Applied)
            .SortBy(x => x.DayStartUtc).ThenBy(x => x.Part).Limit(PendingBatchSize(options)).ToListAsync(cancellationToken);
        if (days.Count == 0) { measurement.Complete("empty"); return; }
        measurement.AddDatabaseOperation();
        var lastProjectedAtUtc = await database.VoiceActivities.Find(x => x.GuildId == guildId && x.UserId == userId && x.ProjectedAtUtc != null)
            .SortByDescending(x => x.ProjectedAtUtc).Project(x => x.ProjectedAtUtc).FirstOrDefaultAsync(cancellationToken);
        if (!ProjectionDue(timeProvider.GetUtcNow().UtcDateTime, lastProjectedAtUtc, projectionInterval, immediate)) { measurement.Complete("deferred"); return; }
        foreach (var day in days) await ProjectAsync(day, displayName, cancellationToken);
        measurement.Complete();
    }

    public async Task ProjectAsync(VoiceActivityDay day, string? displayName, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var owner = Guid.NewGuid().ToString("N");
        var recovering = day.ProjectionStatus == VoiceActivityProjectionStatus.Projecting;
        var claimFilter = Builders<VoiceActivityDay>.Filter.Eq(x => x.Id, day.Id) & (recovering
            ? Builders<VoiceActivityDay>.Filter.Eq(x => x.ProjectionStatus, VoiceActivityProjectionStatus.Projecting) & Builders<VoiceActivityDay>.Filter.Lte(x => x.ProjectionLeaseExpiresAtUtc, now)
            : Builders<VoiceActivityDay>.Filter.Eq(x => x.ProjectionStatus, VoiceActivityProjectionStatus.Pending) & Builders<VoiceActivityDay>.Filter.Eq(x => x.Revision, day.Revision));
        var claimUpdate = Builders<VoiceActivityDay>.Update.Set(x => x.ProjectionStatus, VoiceActivityProjectionStatus.Projecting).Set(x => x.ProjectionLeaseOwner, owner).Set(x => x.ProjectionLeaseExpiresAtUtc, now.Add(LeaseDuration)).Inc(x => x.Revision, 1);
        if (!recovering) claimUpdate = claimUpdate.Set(x => x.ProjectionTargetRevision, day.ProjectionRevision).Set(x => x.ProjectionTargetEligibleSeconds, day.TotalEligibleSeconds)
            .Set(x => x.ProjectionTargetAwardedXp, day.TotalAwardedXp).Set(x => x.ProjectionTargetSeasonTotals, CopyTotals(day.SeasonTotals));
        var claimed = await database.VoiceActivities.FindOneAndUpdateAsync(claimFilter, claimUpdate,
            new FindOneAndUpdateOptions<VoiceActivityDay> { ReturnDocument = ReturnDocument.Before }, cancellationToken);
        if (claimed == null) return;
        var target = recovering ? ProjectionTarget(claimed) : day;

        await using var projectionLease = await coordinator.AcquireAsync(claimed.GuildId, claimed.UserId, null, cancellationToken);
        if (projectionLease == null)
        {
            await ReleaseDayLeaseAsync(claimed.Id!, owner, cancellationToken);
            return;
        }

        var before = await database.MemberXp.Find(x => x.GuildId == claimed.GuildId && x.UserId == claimed.UserId).FirstOrDefaultAsync(cancellationToken) ?? new MemberXp();
        if (recovering && !await RebuildAbsoluteAsync(target, displayName, now, owner, projectionLease, cancellationToken))
        {
            await ReleaseDayLeaseAsync(claimed.Id!, owner, cancellationToken);
            return;
        }
        if (!recovering) await ApplyDeltaAsync(target, displayName, now, cancellationToken);
        if (!await RenewProjectionOwnershipAsync(claimed.Id!, owner, projectionLease, cancellationToken)) return;
        var after = await database.MemberXp.Find(x => x.GuildId == claimed.GuildId && x.UserId == claimed.UserId).FirstOrDefaultAsync(cancellationToken) ?? before;
        var snapshot = new LevelTransitionSnapshot
        {
            PreviousTotalXp = before.TotalXp, NewTotalXp = after.TotalXp,
            PreviousLevel = Mee6LevelCurve.GetLevel(before.TotalXp), NewLevel = Mee6LevelCurve.GetLevel(after.TotalXp)
        };
        var channels = claimed.Segments.Select(x => x.ChannelId).Distinct().Take(2).ToArray();
        var channelId = channels.Length == 1 ? channels[0] : (ulong?)null;
        await transitions.EnsureAsync(claimed.GuildId, claimed.UserId,
            new("voice", $"voice:{claimed.Id}:{target.ProjectionRevision}", after.TotalXp - before.TotalXp, channelId), snapshot, cancellationToken);

        if (!await RenewProjectionOwnershipAsync(claimed.Id!, owner, projectionLease, cancellationToken)) return;
        var projectedTotals = ProjectedTotals(target.SeasonTotals);
        var projected = Builders<VoiceActivityDay>.Update.Set(x => x.ProjectedRevision, target.ProjectionRevision)
            .Set(x => x.ProjectedEligibleSeconds, target.TotalEligibleSeconds).Set(x => x.ProjectedXp, target.TotalAwardedXp)
            .Set(x => x.ProjectedSeasonTotals, projectedTotals).Set(x => x.ProjectedAtUtc, now)
            .Unset(x => x.ProjectionLeaseOwner).Unset(x => x.ProjectionLeaseExpiresAtUtc);
        var applied = await database.VoiceActivities.UpdateOneAsync(x => x.Id == claimed.Id && x.ProjectionLeaseOwner == owner && x.ProjectionRevision == target.ProjectionRevision,
            projected.Set(x => x.SeasonTotals, projectedTotals).Set(x => x.ProjectionStatus, VoiceActivityProjectionStatus.Applied), cancellationToken: cancellationToken);
        if (applied.ModifiedCount == 0)
            await database.VoiceActivities.UpdateOneAsync(x => x.Id == claimed.Id && x.ProjectionLeaseOwner == owner,
                projected.Set(x => x.ProjectionStatus, VoiceActivityProjectionStatus.Pending), cancellationToken: cancellationToken);
        await realtime.PublishMemberAsync(claimed.GuildId, claimed.UserId, cancellationToken);
    }

    private async Task ApplyDeltaAsync(VoiceActivityDay day, string? displayName, DateTime now, CancellationToken cancellationToken)
    {
        var xp = day.TotalAwardedXp - day.ProjectedXp;
        var seconds = day.TotalEligibleSeconds - day.ProjectedEligibleSeconds;
        var memberUpdate = Builders<MemberXp>.Update.SetOnInsert(x => x.GuildId, day.GuildId).SetOnInsert(x => x.UserId, day.UserId)
            .SetOnInsert(x => x.IsCurrentMember, true).SetOnInsert(x => x.PublicLeaderboardVisible, true).Set(x => x.UpdatedAt, now)
            .Inc(x => x.VoiceXp, xp).Inc(x => x.EarnedXp, xp).Inc(x => x.TotalXp, xp).Inc(x => x.VoiceSeconds, seconds);
        if (!string.IsNullOrWhiteSpace(displayName)) memberUpdate = memberUpdate.Set(x => x.DisplayName, displayName).Set(x => x.NormalizedDisplayName, XpService.NormalizeName(displayName));
        await UpsertAsync(database.MemberXp, x => x.GuildId == day.GuildId && x.UserId == day.UserId, memberUpdate, cancellationToken);

        var projectedSeasons = day.ProjectedSeasonTotals.Where(x => x.SeasonId != null).ToDictionary(x => x.SeasonId!, StringComparer.Ordinal);
        foreach (var total in day.SeasonTotals.Where(x => x.SeasonId != null))
        {
            var seasonId = total.SeasonId!;
            var season = await database.GuildSeasons.Find(x => x.Id == seasonId).FirstOrDefaultAsync(cancellationToken);
            if (!CanMutateSeason(season?.Status)) continue;
            projectedSeasons.TryGetValue(seasonId, out var prior);
            var seasonXp = total.AwardedXp - (prior?.AwardedXp ?? 0);
            var seasonSeconds = total.EligibleSeconds - (prior?.EligibleSeconds ?? 0);
            var update = Builders<SeasonMemberXp>.Update.SetOnInsert(x => x.GuildId, day.GuildId).SetOnInsert(x => x.SeasonId, seasonId).SetOnInsert(x => x.UserId, day.UserId)
                .SetOnInsert(x => x.StartingXp, 0).SetOnInsert(x => x.IsCurrentMember, true).SetOnInsert(x => x.PublicLeaderboardVisible, true).Set(x => x.UpdatedAtUtc, now)
                .Inc(x => x.VoiceXp, seasonXp).Inc(x => x.EarnedXp, seasonXp).Inc(x => x.TotalXp, seasonXp).Inc(x => x.VoiceSeconds, seasonSeconds);
            if (!string.IsNullOrWhiteSpace(displayName)) update = update.Set(x => x.DisplayName, displayName);
            await UpsertAsync(database.SeasonMemberXp, x => x.SeasonId == seasonId && x.UserId == day.UserId, update, cancellationToken);
        }
        await UpsertAsync(database.GuildStats, x => x.GuildId == day.GuildId,
            Builders<GuildStats>.Update.SetOnInsert(x => x.GuildId, day.GuildId).Inc(x => x.XpAwarded, xp).Inc(x => x.VoiceXpAwarded, xp).Inc(x => x.VoiceSeconds, seconds), cancellationToken);
    }

    private async Task<bool> RebuildAbsoluteAsync(VoiceActivityDay claimed, string? displayName, DateTime now, string owner, IXpProjectionLease projectionLease, CancellationToken cancellationToken)
    {
        var days = await database.VoiceActivities.Find(x => x.GuildId == claimed.GuildId && x.UserId == claimed.UserId).ToListAsync(cancellationToken);
        var migration = await database.VoiceLedgerMigrationStates.Find(x => x.Id == VoiceLedgerMigrationState.SingletonId).FirstOrDefaultAsync(cancellationToken);
        var compressedAuthoritative = VoiceLedgerMigrationService.IsCompressedVoiceAuthoritative(migration);
        var voice = Snapshot(days, claimed, compressedAuthoritative);
        var ledger = await database.XpLedger.Find(x => x.GuildId == claimed.GuildId && x.UserId == claimed.UserId && !x.IsProjectionControl && !x.CooldownDenied && x.ProjectionStatus == SeasonProjectionStatus.Applied).ToListAsync(cancellationToken);
        if (compressedAuthoritative) ledger.RemoveAll(x => x.Source == "voice");
        var member = await database.MemberXp.Find(x => x.GuildId == claimed.GuildId && x.UserId == claimed.UserId).FirstOrDefaultAsync(cancellationToken) ?? new MemberXp();
        var lifetime = ledger.Where(XpLedgerSemantics.AffectsLifetime).ToArray();
        var nonVoiceEarned = lifetime.Where(XpLedgerSemantics.IsAutomatic).Sum(x => x.Amount);
        var legacyVoice = lifetime.Where(x => XpLedgerSemantics.IsAutomatic(x) && x.Source == "voice").ToArray();
        var totalVoiceXp = legacyVoice.Sum(x => x.Amount) + voice.AwardedXp;
        var totalVoiceSeconds = legacyVoice.Sum(VoiceSeconds) + voice.EligibleSeconds;
        var manual = lifetime.Where(x => !XpLedgerSemantics.IsAutomatic(x)).Sum(x => x.Amount);
        var name = string.IsNullOrWhiteSpace(displayName) ? member.DisplayName : displayName;
        if (!await RenewProjectionOwnershipAsync(claimed.Id!, owner, projectionLease, cancellationToken)) return false;
        await database.MemberXp.UpdateOneAsync(x => x.GuildId == claimed.GuildId && x.UserId == claimed.UserId,
            Builders<MemberXp>.Update.SetOnInsert(x => x.GuildId, claimed.GuildId).SetOnInsert(x => x.UserId, claimed.UserId).Set(x => x.DisplayName, name)
                .Set(x => x.NormalizedDisplayName, XpService.NormalizeName(name)).Set(x => x.VoiceXp, totalVoiceXp).Set(x => x.EarnedXp, nonVoiceEarned + voice.AwardedXp)
                .Set(x => x.ManualAdjustment, manual).Set(x => x.TotalXp, member.ImportedMee6Xp + nonVoiceEarned + voice.AwardedXp + manual).Set(x => x.VoiceSeconds, totalVoiceSeconds)
                .Set(x => x.MessageCount, ledger.LongCount(x => XpLedgerSemantics.IsAutomatic(x) && x.Source == "message")).Set(x => x.UpdatedAt, now), new UpdateOptions { IsUpsert = true }, cancellationToken);

        foreach (var seasonTotal in voice.Seasons.Values)
        {
            var seasonId = seasonTotal.SeasonId!;
            var season = await database.GuildSeasons.Find(x => x.Id == seasonId).FirstOrDefaultAsync(cancellationToken);
            if (!CanMutateSeason(season?.Status)) continue;
            var seasonLedger = ledger.Where(x => x.SeasonId == seasonId && XpLedgerSemantics.AffectsSeason(x)).ToArray();
            var seasonMember = await database.SeasonMemberXp.Find(x => x.SeasonId == seasonId && x.UserId == claimed.UserId).FirstOrDefaultAsync(cancellationToken) ?? new SeasonMemberXp();
            var earned = seasonLedger.Where(XpLedgerSemantics.IsAutomatic).Sum(x => x.Amount) + seasonTotal.AwardedXp;
            var legacySeasonVoice = seasonLedger.Where(x => XpLedgerSemantics.IsAutomatic(x) && x.Source == "voice").ToArray();
            var totalSeasonVoiceXp = legacySeasonVoice.Sum(x => x.Amount) + seasonTotal.AwardedXp;
            var totalSeasonVoiceSeconds = legacySeasonVoice.Sum(VoiceSeconds) + seasonTotal.EligibleSeconds;
            var seasonManual = seasonLedger.Where(x => !XpLedgerSemantics.IsAutomatic(x)).Sum(x => x.Amount);
            if (!await RenewProjectionOwnershipAsync(claimed.Id!, owner, projectionLease, cancellationToken)) return false;
            await database.SeasonMemberXp.UpdateOneAsync(x => x.SeasonId == seasonId && x.UserId == claimed.UserId,
                Builders<SeasonMemberXp>.Update.SetOnInsert(x => x.GuildId, claimed.GuildId).SetOnInsert(x => x.SeasonId, seasonId).SetOnInsert(x => x.UserId, claimed.UserId)
                    .Set(x => x.DisplayName, name).Set(x => x.VoiceXp, totalSeasonVoiceXp).Set(x => x.EarnedXp, earned).Set(x => x.ManualAdjustment, seasonManual)
                    .Set(x => x.TotalXp, seasonMember.StartingXp + earned + seasonManual).Set(x => x.VoiceSeconds, totalSeasonVoiceSeconds).Set(x => x.UpdatedAtUtc, now), new UpdateOptions { IsUpsert = true }, cancellationToken);
        }

        var guildDays = await database.VoiceActivities.Find(x => x.GuildId == claimed.GuildId).ToListAsync(cancellationToken);
        var guildVoice = Snapshot(guildDays, claimed, compressedAuthoritative);
        var guildLedger = await database.XpLedger.Find(x => x.GuildId == claimed.GuildId && !x.IsProjectionControl && !x.CooldownDenied && x.ProjectionStatus == SeasonProjectionStatus.Applied).ToListAsync(cancellationToken);
        if (compressedAuthoritative) guildLedger.RemoveAll(x => x.Source == "voice");
        var automaticXp = guildLedger.Where(XpLedgerSemantics.IsAutomatic).Sum(x => x.Amount);
        var guildLegacyVoice = guildLedger.Where(x => XpLedgerSemantics.IsAutomatic(x) && x.Source == "voice").ToArray();
        var guildTotalVoiceXp = guildLegacyVoice.Sum(x => x.Amount) + guildVoice.AwardedXp;
        var guildTotalVoiceSeconds = guildLegacyVoice.Sum(VoiceSeconds) + guildVoice.EligibleSeconds;
        if (!await RenewProjectionOwnershipAsync(claimed.Id!, owner, projectionLease, cancellationToken)) return false;
        await database.GuildStats.UpdateOneAsync(x => x.GuildId == claimed.GuildId, Builders<GuildStats>.Update.SetOnInsert(x => x.GuildId, claimed.GuildId)
            .Set(x => x.XpAwarded, automaticXp + guildVoice.AwardedXp).Set(x => x.VoiceXpAwarded, guildTotalVoiceXp).Set(x => x.VoiceSeconds, guildTotalVoiceSeconds), new UpdateOptions { IsUpsert = true }, cancellationToken);
        return true;
    }

    public static VoiceProjectionTotals Snapshot(IEnumerable<VoiceActivityDay> days, VoiceActivityDay claimed, bool includeLegacy = true)
    {
        var selected = days.Select(x => SelectProjected(x, claimed, includeLegacy)).ToArray();
        var seasons = selected.SelectMany(x => x.Item3).Where(x => x.SeasonId != null).GroupBy(x => x.SeasonId!, StringComparer.Ordinal).ToDictionary(x => x.Key,
            x => new VoiceSeasonTotal { SeasonId = x.Key, EligibleSeconds = x.Sum(y => y.EligibleSeconds), AwardedXp = x.Sum(y => y.AwardedXp) }, StringComparer.Ordinal);
        return new(selected.Sum(x => x.Item1), selected.Sum(x => x.Item2), seasons);
    }

    private static (long, decimal, List<VoiceSeasonTotal>) SelectProjected(VoiceActivityDay day, VoiceActivityDay claimed, bool includeLegacy)
    {
        var isClaimed = day.Id == claimed.Id;
        var seconds = isClaimed ? claimed.TotalEligibleSeconds : day.ProjectedEligibleSeconds;
        var xp = isClaimed ? claimed.TotalAwardedXp : day.ProjectedXp;
        var seasons = CopyTotals(isClaimed ? claimed.SeasonTotals : day.ProjectedSeasonTotals);
        if (includeLegacy) return (seconds, xp, seasons);
        var legacy = day.Segments.Where(x => x.SettingsRevision == VoiceLedgerMigrationService.LegacySettingsRevision).ToArray();
        seconds -= legacy.Sum(x => x.EligibleSeconds);
        xp -= legacy.Sum(x => x.AwardedXp);
        foreach (var total in legacy.Where(x => x.SeasonId != null).GroupBy(x => x.SeasonId!))
        {
            var season = seasons.SingleOrDefault(x => x.SeasonId == total.Key);
            if (season == null) continue;
            season.EligibleSeconds -= total.Sum(x => x.EligibleSeconds);
            season.AwardedXp -= total.Sum(x => x.AwardedXp);
        }
        seasons.RemoveAll(x => x.EligibleSeconds == 0 && x.AwardedXp == 0);
        return (Math.Max(0, seconds), Math.Max(0, xp), seasons);
    }

    internal static bool CanMutateSeason(SeasonStatus? status) => status != null && status != SeasonStatus.Closed;
    private static long VoiceSeconds(XpLedgerEntry entry) => entry.PeriodStartsAtUtc != null && entry.PeriodEndsAtUtc != null
        ? (long)(entry.PeriodEndsAtUtc.Value - entry.PeriodStartsAtUtc.Value).TotalSeconds : 0;

    internal static VoiceActivityDay ProjectionTarget(VoiceActivityDay day) => new()
    {
        Id = day.Id, GuildId = day.GuildId, UserId = day.UserId, DayStartUtc = day.DayStartUtc, Part = day.Part,
        ProjectionRevision = day.ProjectionTargetRevision, TotalEligibleSeconds = day.ProjectionTargetEligibleSeconds,
        TotalAwardedXp = day.ProjectionTargetAwardedXp, SeasonTotals = CopyTotals(day.ProjectionTargetSeasonTotals),
        ProjectedRevision = day.ProjectedRevision, ProjectedEligibleSeconds = day.ProjectedEligibleSeconds,
        ProjectedXp = day.ProjectedXp, ProjectedSeasonTotals = CopyTotals(day.ProjectedSeasonTotals)
    };

    private Task ReleaseDayLeaseAsync(string id, string owner, CancellationToken cancellationToken) => database.VoiceActivities.UpdateOneAsync(x => x.Id == id && x.ProjectionLeaseOwner == owner,
        Builders<VoiceActivityDay>.Update.Set(x => x.ProjectionStatus, VoiceActivityProjectionStatus.Pending).Unset(x => x.ProjectionLeaseOwner).Unset(x => x.ProjectionLeaseExpiresAtUtc), cancellationToken: cancellationToken);

    private async Task<bool> RenewProjectionOwnershipAsync(string dayId, string owner, IXpProjectionLease projectionLease, CancellationToken cancellationToken)
    {
        if (!await projectionLease.RenewAsync(cancellationToken)) return false;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var renewed = await database.VoiceActivities.UpdateOneAsync(x => x.Id == dayId && x.ProjectionLeaseOwner == owner && x.ProjectionLeaseExpiresAtUtc > now,
            Builders<VoiceActivityDay>.Update.Set(x => x.ProjectionLeaseExpiresAtUtc, now.Add(LeaseDuration)), cancellationToken: cancellationToken);
        return renewed.MatchedCount == 1;
    }

    internal static bool ProjectionDue(DateTime nowUtc, DateTime? lastProjectedAtUtc, TimeSpan interval, bool immediate) =>
        immediate || lastProjectedAtUtc == null || nowUtc - lastProjectedAtUtc.Value >= interval;

    internal static int PendingBatchSize(VoiceActivityOptions options) => options.ProjectionBatchSize;

    private static List<VoiceSeasonTotal> CopyTotals(IEnumerable<VoiceSeasonTotal> values) => values.Select(x => new VoiceSeasonTotal { SeasonId = x.SeasonId, EligibleSeconds = x.EligibleSeconds, AwardedXp = x.AwardedXp, ProjectedEligibleSeconds = x.ProjectedEligibleSeconds, ProjectedXp = x.ProjectedXp }).ToList();
    private static List<VoiceSeasonTotal> ProjectedTotals(IEnumerable<VoiceSeasonTotal> values) => values.Select(x => new VoiceSeasonTotal { SeasonId = x.SeasonId, EligibleSeconds = x.EligibleSeconds, AwardedXp = x.AwardedXp, ProjectedEligibleSeconds = x.EligibleSeconds, ProjectedXp = x.AwardedXp }).ToList();
    private static async Task UpsertAsync<T>(IMongoCollection<T> collection, System.Linq.Expressions.Expression<Func<T, bool>> filter, UpdateDefinition<T> update, CancellationToken cancellationToken)
    {
        try { await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken); }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { await collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken); }
    }
}
