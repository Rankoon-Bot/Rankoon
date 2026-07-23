using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;

namespace Rankoon.Data.Xp;

public interface IXpService
{
    Task<GuildXpSettings> GetSettingsAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(GuildXpSettings settings, CancellationToken cancellationToken = default);
    Task<bool> GrantAsync(ulong guildId, ulong userId, string displayName, string source, decimal amount, string key, ulong? channelId = null, CancellationToken cancellationToken = default);
    Task<bool> GrantAsync(XpGrantRequest request, CancellationToken cancellationToken = default);
    Task<bool> ReverseGrantAsync(string originalGrantKey, string reversalGrantKey, CancellationToken cancellationToken = default);
    Task<bool> ReverseGrantAsync(string originalGrantKey, string reversalGrantKey, string expectedSource, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemberXp>> GetLeaderboardAsync(ulong guildId, int take, CancellationToken cancellationToken = default);
    Task<MemberXp?> GetMemberAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default);
    Task RecalculateTotalAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default);
}

public sealed record XpGrantRequest(ulong GuildId, ulong UserId, string DisplayName, string Source, decimal Amount, string GrantKey, DateTime OccurredAtUtc, ulong? ChannelId = null, DateTime? PeriodStartsAtUtc = null, DateTime? PeriodEndsAtUtc = null, string? ReversesGrantKey = null, int? CooldownSeconds = null, bool SuppressReport = false, decimal? AppliedServerBoosterMultiplier = null);

public sealed class XpService(RankoonDbContext database, ISeasonService seasons, IReportWriter reports, ILeaderboardRealtimePublisher realtime, ILevelTransitionService transitions, TimeProvider timeProvider, ILogger<XpService> logger) : IXpService
{
    public async Task<GuildXpSettings> GetSettingsAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var settings = await database.GuildXpSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        settings ??= new GuildXpSettings { GuildId = guildId };
        settings.ServerBooster ??= new ServerBoosterXpSettings();
        settings.ServerBooster.Tiers ??= [];
        ServerBoosterXpSettingsValidator.Normalize(settings.ServerBooster);
        return settings;
    }

    public Task SaveSettingsAsync(GuildXpSettings settings, CancellationToken cancellationToken = default)
    {
        var updatedAt = timeProvider.GetUtcNow().UtcDateTime;
        settings.UpdatedAt = updatedAt;
        var update = Builders<GuildXpSettings>.Update
            .SetOnInsert(x => x.GuildId, settings.GuildId)
            .Set(x => x.Enabled, settings.Enabled)
            .Set(x => x.Message, settings.Message)
            .Set(x => x.Voice, settings.Voice)
            .Set(x => x.Reaction, settings.Reaction)
            .Set(x => x.EventInterest, settings.EventInterest)
            .Set(x => x.Thread, settings.Thread)
            .Set(x => x.ExcludedChannelIds, settings.ExcludedChannelIds)
            .Set(x => x.ExcludedCategoryIds, settings.ExcludedCategoryIds)
            .Set(x => x.ExcludedRoleIds, settings.ExcludedRoleIds)
            .Set(x => x.ChannelMultipliers, settings.ChannelMultipliers)
            .Set(x => x.ServerBooster, settings.ServerBooster)
            .Set(x => x.LevelRoles, settings.LevelRoles)
            .Set(x => x.LevelUpChannelId, settings.LevelUpChannelId)
            .Set(x => x.UpdatedAt, updatedAt);
        return database.GuildXpSettings.UpdateOneAsync(x => x.GuildId == settings.GuildId, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    public async Task<bool> GrantAsync(ulong guildId, ulong userId, string displayName, string source, decimal amount, string key, ulong? channelId = null, CancellationToken cancellationToken = default)
        => await GrantAsync(new XpGrantRequest(guildId, userId, displayName, source, amount, key, timeProvider.GetUtcNow().UtcDateTime, channelId), cancellationToken);

    public async Task<bool> GrantAsync(XpGrantRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Amount == 0) return false;
        var occurredAtUtc = DateTime.SpecifyKind(request.OccurredAtUtc, DateTimeKind.Utc);
        var season = await seasons.ResolveAsync(request.GuildId, occurredAtUtc, cancellationToken);
        XpLedgerEntry ledger;
        var inserted = false;
        try
        {
            ledger = new XpLedgerEntry
            {
                GrantKey = request.GrantKey, GuildId = request.GuildId, UserId = request.UserId, DisplayName = request.DisplayName, Source = request.Source,
                Amount = request.Amount, AppliedServerBoosterMultiplier = request.AppliedServerBoosterMultiplier is > 1m ? request.AppliedServerBoosterMultiplier : null,
                ChannelId = request.ChannelId, OccurredAtUtc = occurredAtUtc, CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
                SeasonId = season?.Id, PeriodStartsAtUtc = request.PeriodStartsAtUtc, PeriodEndsAtUtc = request.PeriodEndsAtUtc, ReversesGrantKey = request.ReversesGrantKey,
                CooldownSource = request.CooldownSeconds.HasValue ? request.Source : null, CooldownSeconds = request.CooldownSeconds,
                Kind = XpLedgerEntryKind.AutomaticGrant, Scope = XpLedgerScope.LifetimeAndSeason
            };
            await database.XpLedger.InsertOneAsync(ledger, cancellationToken: cancellationToken);
            inserted = true;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            ledger = await database.XpLedger.Find(x => x.GrantKey == request.GrantKey).FirstOrDefaultAsync(cancellationToken) ?? throw new InvalidOperationException("Duplicate ledger entry could not be read.");
            if (ledger.ProjectionStatus == SeasonProjectionStatus.Applied || ledger.CooldownDenied) return false;
        }
        if (ledger.CooldownSeconds is > 0 && !ledger.CooldownAcquired)
        {
            if (!await AcquireCooldownAsync(ledger, cancellationToken))
            {
                await database.XpLedger.UpdateOneAsync(x => x.Id == ledger.Id && !x.CooldownAcquired,
                    Builders<XpLedgerEntry>.Update.Set(x => x.CooldownDenied, true).Set(x => x.ProjectionStatus, SeasonProjectionStatus.Applied).Set(x => x.ProjectedAtUtc, timeProvider.GetUtcNow().UtcDateTime), cancellationToken: cancellationToken);
                return false;
            }
            ledger.CooldownAcquired = true;
            await database.XpLedger.UpdateOneAsync(x => x.Id == ledger.Id, Builders<XpLedgerEntry>.Update.Set(x => x.CooldownAcquired, true), cancellationToken: cancellationToken);
        }
        await ProjectAsync(ledger, cancellationToken);
        if (inserted && !request.SuppressReport) await reports.WriteAsync(new(request.GuildId, ReportCategories.Activity, ReportNames.XpGranted, ReportOutcomes.Succeeded, request.Source, request.UserId, Metadata: new Dictionary<string, object?>
        {
            ["source"] = request.Source,
            ["amount"] = request.Amount,
            ["channelId"] = request.ChannelId,
            ["seasonId"] = ledger.SeasonId
        }, SubjectId: request.UserId, ChannelId: request.ChannelId), cancellationToken);
        logger.LogDebug("Granted {Amount} {Source} XP to {UserId} in {GuildId}", request.Amount, request.Source, request.UserId, request.GuildId);
        return true;
    }

    public Task<bool> ReverseGrantAsync(string originalGrantKey, string reversalGrantKey, CancellationToken cancellationToken = default) =>
        ReverseGrantAsync(originalGrantKey, reversalGrantKey, null, cancellationToken);

    public async Task<bool> ReverseGrantAsync(string originalGrantKey, string reversalGrantKey, string? expectedSource, CancellationToken cancellationToken = default)
    {
        var original = await database.XpLedger.Find(x => x.GrantKey == originalGrantKey).FirstOrDefaultAsync(cancellationToken);
        if (original == null) return false;
        if (expectedSource != null && !MatchesAutomaticGrant(original, expectedSource)) return false;
        // Cooldown-denied entries are retained for idempotency and auditability, but never awarded XP.
        if (original.CooldownDenied) return false;
        var reversal = new XpGrantRequest(original.GuildId, original.UserId, original.DisplayName, $"{original.Source}_reversal", -original.Amount, reversalGrantKey,
            timeProvider.GetUtcNow().UtcDateTime, original.ChannelId, ReversesGrantKey: originalGrantKey);
        // Preserve the original immutable season attribution even if a different season is currently active.
        var existing = await database.XpLedger.Find(x => x.GrantKey == reversalGrantKey).FirstOrDefaultAsync(cancellationToken);
        if (existing != null) { if (existing.ProjectionStatus == SeasonProjectionStatus.Pending) await ProjectAsync(existing, cancellationToken); return false; }
        var entry = CreateAutomaticReversal(original, reversalGrantKey, reversal.OccurredAtUtc);
        try { await database.XpLedger.InsertOneAsync(entry, cancellationToken: cancellationToken); }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { return false; }
        await ProjectAsync(entry, cancellationToken);
        return true;
    }

    internal static bool MatchesAutomaticGrant(XpLedgerEntry entry, string source) =>
        entry.Source == source && XpLedgerSemantics.GetEffectiveKind(entry) == XpLedgerEntryKind.AutomaticGrant;

    internal static XpLedgerEntry CreateAutomaticReversal(XpLedgerEntry original, string reversalGrantKey, DateTime occurredAtUtc) => new()
    {
        GrantKey = reversalGrantKey, GuildId = original.GuildId, UserId = original.UserId, DisplayName = original.DisplayName,
        Source = $"{original.Source}_reversal", Amount = -original.Amount, ChannelId = original.ChannelId,
        OccurredAtUtc = occurredAtUtc, CreatedAt = occurredAtUtc, SeasonId = original.SeasonId,
        ReversesGrantKey = original.GrantKey, ReversesLedgerEntryId = original.Id,
        Kind = XpLedgerEntryKind.AutomaticReversal, Scope = XpLedgerSemantics.GetEffectiveScope(original)
    };

    private async Task<bool> AcquireCooldownAsync(XpLedgerEntry ledger, CancellationToken cancellationToken)
    {
        var fields = ledger.CooldownSource switch
        {
            "message" => ("last_message_xp_at", "last_message_xp_grant_key"),
            "reaction" => ("last_reaction_xp_at", "last_reaction_xp_grant_key"),
            "thread_message" => ("last_thread_xp_at", "last_thread_xp_grant_key"),
            _ => throw new InvalidOperationException($"Unsupported XP cooldown source '{ledger.CooldownSource}'.")
        };
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var filter = Builders<MemberXp>.Filter.And(
            Builders<MemberXp>.Filter.Eq(x => x.GuildId, ledger.GuildId),
            Builders<MemberXp>.Filter.Eq(x => x.UserId, ledger.UserId),
            new BsonDocument("$or", new BsonArray { new BsonDocument(fields.Item1, new BsonDocument("$exists", false)), new BsonDocument(fields.Item1, BsonNull.Value), new BsonDocument(fields.Item1, new BsonDocument("$lte", now.AddSeconds(-ledger.CooldownSeconds!.Value))) }));
        try
        {
            var result = await database.MemberXp.UpdateOneAsync(filter, new BsonDocument("$set", new BsonDocument { { fields.Item1, now }, { fields.Item2, ledger.GrantKey } }), new UpdateOptions { IsUpsert = true }, cancellationToken);
            if (result.MatchedCount != 0 || result.UpsertedId != null) return true;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { }

        // A process can fail after the member CAS and before setting CooldownAcquired on the ledger.
        var member = await database.MemberXp.Find(Builders<MemberXp>.Filter.And(
            Builders<MemberXp>.Filter.Eq(x => x.GuildId, ledger.GuildId),
            Builders<MemberXp>.Filter.Eq(x => x.UserId, ledger.UserId),
            new BsonDocument(fields.Item2, ledger.GrantKey))).FirstOrDefaultAsync(cancellationToken);
        return member != null;
    }

    internal async Task ProjectAsync(XpLedgerEntry ledger, CancellationToken cancellationToken)
    {
        if (ledger.CooldownDenied || ledger.ProjectionStatus == SeasonProjectionStatus.Applied) return;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var owner = Guid.NewGuid().ToString("N");
        var claimed = await database.XpLedger.FindOneAndUpdateAsync(
            Builders<XpLedgerEntry>.Filter.And(
                Builders<XpLedgerEntry>.Filter.Eq(x => x.Id, ledger.Id),
                Builders<XpLedgerEntry>.Filter.Eq(x => x.ProjectionStatus, SeasonProjectionStatus.Pending),
                Builders<XpLedgerEntry>.Filter.Or(
                    Builders<XpLedgerEntry>.Filter.Eq(x => x.ProjectionLeaseOwner, null),
                    Builders<XpLedgerEntry>.Filter.Lte(x => x.ProjectionLeaseExpiresAtUtc, now))),
            Builders<XpLedgerEntry>.Update.Set(x => x.ProjectionLeaseOwner, owner).Set(x => x.ProjectionLeaseExpiresAtUtc, now.AddMinutes(2)),
            new FindOneAndUpdateOptions<XpLedgerEntry> { ReturnDocument = ReturnDocument.Before }, cancellationToken);
        if (claimed == null) return;

        var recovering = claimed.ProjectionLeaseOwner != null;
        var locks = await AcquireProjectionLocksAsync(ledger, owner, cancellationToken);
        if (locks == null)
        {
            await ReleaseLeaseAsync(ledger.GrantKey, owner, cancellationToken);
            return;
        }
        try
        {
            var before = await GetMemberAsync(ledger.GuildId, ledger.UserId, cancellationToken) ?? new MemberXp();
            if (recovering) await RebuildProjectionAsync(ledger, now, cancellationToken);
            else await IncrementProjectionAsync(ledger, now, cancellationToken);
            var after = await GetMemberAsync(ledger.GuildId, ledger.UserId, cancellationToken) ?? before;
            var snapshot = ledger.LevelTransitionSnapshot ?? new LevelTransitionSnapshot
            {
                PreviousTotalXp = before.TotalXp,
                NewTotalXp = after.TotalXp,
                PreviousLevel = Mee6LevelCurve.GetLevel(before.TotalXp),
                NewLevel = Mee6LevelCurve.GetLevel(after.TotalXp)
            };
            await database.XpLedger.UpdateOneAsync(x => x.Id == ledger.Id, Builders<XpLedgerEntry>.Update.Set(x => x.LevelTransitionSnapshot, snapshot), cancellationToken: cancellationToken);
            await transitions.EnsureAsync(ledger, snapshot, cancellationToken);
            await database.XpLedger.UpdateOneAsync(x => x.Id == ledger.Id && x.ProjectionStatus == SeasonProjectionStatus.Pending && x.ProjectionLeaseOwner == owner,
                Builders<XpLedgerEntry>.Update.Set(x => x.ProjectionStatus, SeasonProjectionStatus.Applied).Set(x => x.ProjectedAtUtc, now).Unset(x => x.ProjectionLeaseOwner).Unset(x => x.ProjectionLeaseExpiresAtUtc), cancellationToken: cancellationToken);
            await realtime.PublishMemberAsync(ledger.GuildId, ledger.UserId, cancellationToken);
        }
        finally
        {
            foreach (var key in locks) await ReleaseLeaseAsync(key, owner, cancellationToken);
        }
    }

    private async Task IncrementProjectionAsync(XpLedgerEntry ledger, DateTime now, CancellationToken cancellationToken)
    {
        var memberUpdate = Builders<MemberXp>.Update
            .SetOnInsert(x => x.GuildId, ledger.GuildId).SetOnInsert(x => x.UserId, ledger.UserId)
            .SetOnInsert(x => x.PublicLeaderboardVisible, true)
            .Set(x => x.DisplayName, ledger.DisplayName).Set(x => x.NormalizedDisplayName, NormalizeName(ledger.DisplayName)).Set(x => x.IsCurrentMember, true).Set(x => x.UpdatedAt, now);
        if (XpLedgerSemantics.AffectsLifetime(ledger)) memberUpdate = XpLedgerSemantics.IsAutomatic(ledger)
            ? memberUpdate.Inc(x => x.EarnedXp, ledger.Amount).Inc(x => x.TotalXp, ledger.Amount)
            : memberUpdate.Inc(x => x.ManualAdjustment, ledger.Amount).Inc(x => x.TotalXp, ledger.Amount);
        if (XpLedgerSemantics.IsAutomatic(ledger) && ledger.Source == "message") memberUpdate = memberUpdate.Inc(x => x.MessageCount, 1);
        var voiceSeconds = VoiceSeconds(ledger);
        if (XpLedgerSemantics.IsAutomatic(ledger) && voiceSeconds != 0) memberUpdate = memberUpdate.Inc(x => x.VoiceSeconds, voiceSeconds);
        await UpsertAsync(database.MemberXp, Builders<MemberXp>.Filter.And(Builders<MemberXp>.Filter.Eq(x => x.GuildId, ledger.GuildId), Builders<MemberXp>.Filter.Eq(x => x.UserId, ledger.UserId)), memberUpdate, cancellationToken);

        if (XpLedgerSemantics.AffectsSeason(ledger))
        {
            var season = await database.GuildSeasons.Find(x => x.Id == ledger.SeasonId).FirstOrDefaultAsync(cancellationToken);
            if (season != null && season.Status != SeasonStatus.Closed && (!XpLedgerSemantics.IsAutomatic(ledger) ? season.Status == SeasonStatus.Active : true))
            {
                var seasonUpdate = Builders<SeasonMemberXp>.Update
                    .SetOnInsert(x => x.GuildId, ledger.GuildId).SetOnInsert(x => x.SeasonId, ledger.SeasonId).SetOnInsert(x => x.UserId, ledger.UserId)
                    .SetOnInsert(x => x.StartingXp, 0m).SetOnInsert(x => x.PublicLeaderboardVisible, true).Set(x => x.DisplayName, ledger.DisplayName).Set(x => x.IsCurrentMember, true).Set(x => x.UpdatedAtUtc, now)
                    ;
                seasonUpdate = XpLedgerSemantics.IsAutomatic(ledger) ? seasonUpdate.Inc(x => x.EarnedXp, ledger.Amount).Inc(x => x.TotalXp, ledger.Amount) : seasonUpdate.Inc(x => x.ManualAdjustment, ledger.Amount).Inc(x => x.TotalXp, ledger.Amount);
                if (XpLedgerSemantics.IsAutomatic(ledger) && ledger.Source == "message") seasonUpdate = seasonUpdate.Inc(x => x.MessageCount, 1);
                if (XpLedgerSemantics.IsAutomatic(ledger) && voiceSeconds != 0) seasonUpdate = seasonUpdate.Inc(x => x.VoiceSeconds, voiceSeconds);
                await UpsertAsync(database.SeasonMemberXp, Builders<SeasonMemberXp>.Filter.And(Builders<SeasonMemberXp>.Filter.Eq(x => x.SeasonId, ledger.SeasonId), Builders<SeasonMemberXp>.Filter.Eq(x => x.UserId, ledger.UserId)), seasonUpdate, cancellationToken);
            }
        }
        var statsUpdate = Builders<GuildStats>.Update.SetOnInsert(x => x.GuildId, ledger.GuildId);
        if (XpLedgerSemantics.IsAutomatic(ledger)) statsUpdate = statsUpdate.Inc(x => x.XpAwarded, ledger.Amount);
        if (ledger.Source == "message") statsUpdate = statsUpdate.Inc(x => x.Messages, 1);
        if (ledger.Source == "reaction") statsUpdate = statsUpdate.Inc(x => x.Reactions, 1);
        if (ledger.Source.StartsWith("thread", StringComparison.Ordinal)) statsUpdate = statsUpdate.Inc(x => x.Threads, 1);
        if (ledger.Source == "event_interest") statsUpdate = statsUpdate.Inc(x => x.EventInterests, 1);
        await UpsertAsync(database.GuildStats, Builders<GuildStats>.Filter.Eq(x => x.GuildId, ledger.GuildId), statsUpdate, cancellationToken);
    }

    private static long VoiceSeconds(XpLedgerEntry ledger) => ledger.Source == "voice" && ledger.PeriodStartsAtUtc != null && ledger.PeriodEndsAtUtc != null
        ? (long)(ledger.PeriodEndsAtUtc.Value - ledger.PeriodStartsAtUtc.Value).TotalSeconds : 0;

    private async Task RebuildProjectionAsync(XpLedgerEntry ledger, DateTime now, CancellationToken cancellationToken)
    {
        var included = Builders<XpLedgerEntry>.Filter.And(
            Builders<XpLedgerEntry>.Filter.Ne(x => x.IsProjectionControl, true),
            Builders<XpLedgerEntry>.Filter.Ne(x => x.CooldownDenied, true),
            Builders<XpLedgerEntry>.Filter.Or(Builders<XpLedgerEntry>.Filter.Eq(x => x.ProjectionStatus, SeasonProjectionStatus.Applied), Builders<XpLedgerEntry>.Filter.Eq(x => x.Id, ledger.Id)));
        var memberEntries = await database.XpLedger.Find(Builders<XpLedgerEntry>.Filter.And(included, Builders<XpLedgerEntry>.Filter.Eq(x => x.GuildId, ledger.GuildId), Builders<XpLedgerEntry>.Filter.Eq(x => x.UserId, ledger.UserId))).ToListAsync(cancellationToken);
        var member = await GetMemberAsync(ledger.GuildId, ledger.UserId, cancellationToken) ?? new MemberXp { GuildId = ledger.GuildId, UserId = ledger.UserId };
        var lifetimeEntries = memberEntries.Where(XpLedgerSemantics.AffectsLifetime).ToList();
        var earned = lifetimeEntries.Where(XpLedgerSemantics.IsAutomatic).Sum(x => x.Amount);
        var manual = lifetimeEntries.Where(x => !XpLedgerSemantics.IsAutomatic(x)).Sum(x => x.Amount);
        await database.MemberXp.UpdateOneAsync(x => x.GuildId == ledger.GuildId && x.UserId == ledger.UserId,
            Builders<MemberXp>.Update.SetOnInsert(x => x.GuildId, ledger.GuildId).SetOnInsert(x => x.UserId, ledger.UserId).Set(x => x.DisplayName, ledger.DisplayName)
                .Set(x => x.NormalizedDisplayName, NormalizeName(ledger.DisplayName)).Set(x => x.EarnedXp, earned).Set(x => x.ManualAdjustment, manual).Set(x => x.TotalXp, member.ImportedMee6Xp + earned + manual).Set(x => x.MessageCount, memberEntries.LongCount(x => XpLedgerSemantics.IsAutomatic(x) && x.Source == "message"))
                .Set(x => x.VoiceSeconds, memberEntries.Where(XpLedgerSemantics.IsAutomatic).Sum(VoiceSeconds)).Set(x => x.IsCurrentMember, true).Set(x => x.UpdatedAt, now), new UpdateOptions { IsUpsert = true }, cancellationToken);

        if (ledger.SeasonId != null)
        {
            var season = await database.GuildSeasons.Find(x => x.Id == ledger.SeasonId).FirstOrDefaultAsync(cancellationToken);
            if (season != null && season.Status != SeasonStatus.Closed)
            {
                var seasonEntries = memberEntries.Where(x => x.SeasonId == ledger.SeasonId && XpLedgerSemantics.AffectsSeason(x)).ToList();
                var seasonMember = await database.SeasonMemberXp.Find(x => x.SeasonId == ledger.SeasonId && x.UserId == ledger.UserId).FirstOrDefaultAsync(cancellationToken) ?? new SeasonMemberXp();
                var seasonEarned = seasonEntries.Where(XpLedgerSemantics.IsAutomatic).Sum(x => x.Amount);
                var seasonManual = seasonEntries.Where(x => !XpLedgerSemantics.IsAutomatic(x)).Sum(x => x.Amount);
                await database.SeasonMemberXp.UpdateOneAsync(x => x.SeasonId == ledger.SeasonId && x.UserId == ledger.UserId,
                    Builders<SeasonMemberXp>.Update.SetOnInsert(x => x.GuildId, ledger.GuildId).SetOnInsert(x => x.SeasonId, ledger.SeasonId).SetOnInsert(x => x.UserId, ledger.UserId).SetOnInsert(x => x.StartingXp, 0m)
                        .Set(x => x.DisplayName, ledger.DisplayName).Set(x => x.EarnedXp, seasonEarned).Set(x => x.ManualAdjustment, seasonManual).Set(x => x.TotalXp, seasonMember.StartingXp + seasonEarned + seasonManual)
                        .Set(x => x.MessageCount, seasonEntries.LongCount(x => XpLedgerSemantics.IsAutomatic(x) && x.Source == "message")).Set(x => x.VoiceSeconds, seasonEntries.Where(XpLedgerSemantics.IsAutomatic).Sum(VoiceSeconds)).Set(x => x.IsCurrentMember, true).Set(x => x.UpdatedAtUtc, now), new UpdateOptions { IsUpsert = true }, cancellationToken);
            }
        }

        var guildEntries = await database.XpLedger.Find(Builders<XpLedgerEntry>.Filter.And(included, Builders<XpLedgerEntry>.Filter.Eq(x => x.GuildId, ledger.GuildId))).ToListAsync(cancellationToken);
        await database.GuildStats.UpdateOneAsync(x => x.GuildId == ledger.GuildId,
            Builders<GuildStats>.Update.SetOnInsert(x => x.GuildId, ledger.GuildId).Set(x => x.XpAwarded, guildEntries.Sum(x => x.Amount))
                .Set(x => x.Messages, guildEntries.LongCount(x => x.Source == "message")).Set(x => x.Reactions, guildEntries.LongCount(x => x.Source == "reaction"))
                .Set(x => x.Threads, guildEntries.LongCount(x => x.Source.StartsWith("thread", StringComparison.Ordinal))).Set(x => x.EventInterests, guildEntries.LongCount(x => x.Source == "event_interest")), new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    private async Task<IReadOnlyList<string>?> AcquireProjectionLocksAsync(XpLedgerEntry ledger, string owner, CancellationToken cancellationToken)
    {
        var keys = new List<string> { $"projection-lock:guild:{ledger.GuildId}", $"projection-lock:member:{ledger.GuildId}:{ledger.UserId}" };
        if (ledger.SeasonId != null) keys.Add($"projection-lock:season:{ledger.SeasonId}:{ledger.UserId}");
        foreach (var key in keys)
        {
            if (await AcquireLeaseAsync(key, ledger.GuildId, ledger.UserId, owner, cancellationToken)) continue;
            foreach (var acquired in keys.TakeWhile(x => x != key)) await ReleaseLeaseAsync(acquired, owner, cancellationToken);
            return null;
        }
        return keys;
    }

    private async Task<bool> AcquireLeaseAsync(string key, ulong guildId, ulong userId, string owner, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var filter = Builders<XpLedgerEntry>.Filter.And(Builders<XpLedgerEntry>.Filter.Eq(x => x.GrantKey, key), Builders<XpLedgerEntry>.Filter.Or(Builders<XpLedgerEntry>.Filter.Eq(x => x.ProjectionLeaseOwner, null), Builders<XpLedgerEntry>.Filter.Lte(x => x.ProjectionLeaseExpiresAtUtc, now)));
        var update = Builders<XpLedgerEntry>.Update.SetOnInsert(x => x.GrantKey, key).SetOnInsert(x => x.GuildId, guildId).SetOnInsert(x => x.UserId, userId).SetOnInsert(x => x.Source, "projection_control").SetOnInsert(x => x.IsProjectionControl, true).SetOnInsert(x => x.ProjectionStatus, SeasonProjectionStatus.Applied).SetOnInsert(x => x.CreatedAt, now).Set(x => x.ProjectionLeaseOwner, owner).Set(x => x.ProjectionLeaseExpiresAtUtc, now.AddMinutes(2));
        try
        {
            var result = await database.XpLedger.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
            return result.MatchedCount != 0 || result.UpsertedId != null;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { return false; }
    }

    private Task ReleaseLeaseAsync(string key, string owner, CancellationToken cancellationToken) => database.XpLedger.UpdateOneAsync(
        x => x.GrantKey == key && x.ProjectionLeaseOwner == owner,
        Builders<XpLedgerEntry>.Update.Unset(x => x.ProjectionLeaseOwner).Unset(x => x.ProjectionLeaseExpiresAtUtc), cancellationToken: cancellationToken);

    private static async Task UpsertAsync<T>(IMongoCollection<T> collection, FilterDefinition<T> filter, UpdateDefinition<T> update, CancellationToken cancellationToken)
    {
        try { await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken); }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { await collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken); }
    }

    internal static string NormalizeName(string displayName) => displayName.Trim().ToLowerInvariant();


    public async Task<IReadOnlyList<MemberXp>> GetLeaderboardAsync(ulong guildId, int take, CancellationToken cancellationToken = default) =>
        await database.MemberXp.Find(x => x.GuildId == guildId && x.IsCurrentMember)
            .SortByDescending(x => x.TotalXp).ThenBy(x => x.DisplayName).ThenBy(x => x.UserId)
            .Limit(Math.Clamp(take, 1, 100)).ToListAsync(cancellationToken);

    public async Task<MemberXp?> GetMemberAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default) => await database.MemberXp.Find(x => x.GuildId == guildId && x.UserId == userId).FirstOrDefaultAsync(cancellationToken);

    public Task RecalculateTotalAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default)
    {
        var update = new PipelineUpdateDefinition<MemberXp>(new BsonDocument[]
        {
            new BsonDocument("$set", new BsonDocument("total_xp", new BsonDocument("$add", new BsonArray
            {
                new BsonDocument("$ifNull", new BsonArray { "$imported_mee6_xp", 0 }),
                new BsonDocument("$ifNull", new BsonArray { "$earned_xp", 0 }),
                new BsonDocument("$ifNull", new BsonArray { "$manual_adjustment", 0 })
            })))
        });
        return database.MemberXp.UpdateOneAsync(x => x.GuildId == guildId && x.UserId == userId, update, cancellationToken: cancellationToken);
    }
}
