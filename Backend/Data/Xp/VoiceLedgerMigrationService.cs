using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Operations;

namespace Rankoon.Data.Xp;

public sealed class VoiceLedgerMigrationService(
    RankoonDbContext database,
    VoiceActivityAccumulator accumulator,
    IOperationalErrorRecorder errors,
    IWorkerHealthRegistry health,
    TimeProvider timeProvider,
    IOptions<VoiceLedgerMigrationOptions> configuredOptions,
    IOptions<VoiceActivityOptions> configuredActivityOptions,
    ILogger<VoiceLedgerMigrationService> logger) : BackgroundService
{
    internal const string WorkerName = "voice-ledger-migration";
    internal const long LegacySettingsRevision = long.MinValue;
    internal const string RequiredActivityIndex = "guild_user_day_part_unique";
    private readonly VoiceLedgerMigrationOptions options = Validate(configuredOptions.Value);
    private readonly int migrationBatchSize = configuredOptions.Value.CopyBatchSize ?? configuredActivityOptions.Value.MigrationBatchSize;
    private readonly IMongoCollection<XpLedgerEntry> ledger = database.XpLedger;
    private readonly IMongoCollection<VoiceActivityDay> activities = database.VoiceActivities;
    private readonly IMongoCollection<VoiceLedgerMigrationState> states = database.VoiceLedgerMigrationStates;

    public async Task<bool> ApproveDeletionAsync(string parityFingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parityFingerprint);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var result = await states.UpdateOneAsync(
            x => x.Id == VoiceLedgerMigrationState.SingletonId &&
                 x.SchemaVersion == VoiceLedgerMigrationState.CurrentSchemaVersion &&
                 x.Phase == VoiceLedgerMigrationPhase.AwaitingDeletionApproval &&
                 x.Parity != null && x.Parity.Matches && x.Parity.GroupedParityVerified && x.Parity.Fingerprint == parityFingerprint,
            Builders<VoiceLedgerMigrationState>.Update
                .Set(x => x.DeletionApprovedAtUtc, now)
                .Set(x => x.DeletionApprovalFingerprint, parityFingerprint)
                .Set(x => x.Phase, VoiceLedgerMigrationPhase.Deleting)
                .Set(x => x.UpdatedAtUtc, now),
            cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await HasRequiredIndexAsync(stoppingToken))
                {
                    health.Report(WorkerName, WorkerHealthState.Degraded, $"Waiting for index {RequiredActivityIndex}");
                    await Task.Delay(TimeSpan.FromSeconds(options.IdleDelaySeconds), timeProvider, stoppingToken);
                    continue;
                }
                var state = await GetOrCreateStateAsync(stoppingToken);
                var didWork = state.Phase switch
                {
                    VoiceLedgerMigrationPhase.Copying => await CopyBatchAsync(state, stoppingToken),
                    VoiceLedgerMigrationPhase.Verifying or VoiceLedgerMigrationPhase.ParityFailed => await VerifyAsync(state, stoppingToken),
                    VoiceLedgerMigrationPhase.AwaitingDeletionApproval when state.Parity?.GroupedParityVerified != true => await VerifyAsync(state, stoppingToken),
                    VoiceLedgerMigrationPhase.Deleting => await DeleteBatchAsync(state, stoppingToken),
                    _ => false
                };
                consecutiveFailures = 0;
                health.Report(WorkerName, state.Phase == VoiceLedgerMigrationPhase.ParityFailed ? WorkerHealthState.Degraded : WorkerHealthState.Healthy, state.Phase.ToString());
                await Task.Delay(didWork ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(options.IdleDelaySeconds), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                logger.LogError(exception, "Legacy voice ledger migration failed");
                health.Report(WorkerName, consecutiveFailures >= options.MaxRetries ? WorkerHealthState.Unhealthy : WorkerHealthState.Degraded,
                    $"{exception.GetBaseException().GetType().Name}; retry {consecutiveFailures}");
                try
                {
                    await RecordFailureAsync(exception, stoppingToken);
                    await errors.RecordAsync(new(exception, "worker", "xp.voice-ledger-migration", Worker: WorkerName), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception recordingException)
                {
                    logger.LogWarning(recordingException, "Could not persist legacy voice migration failure diagnostics");
                }
                await Task.Delay(TimeSpan.FromSeconds(options.RetryDelaySeconds), timeProvider, stoppingToken);
            }
        }
    }

    private async Task<VoiceLedgerMigrationState> GetOrCreateStateAsync(CancellationToken cancellationToken)
    {
        var state = await states.Find(x => x.Id == VoiceLedgerMigrationState.SingletonId).FirstOrDefaultAsync(cancellationToken);
        if (state != null)
        {
            if (state.SchemaVersion != VoiceLedgerMigrationState.CurrentSchemaVersion)
                throw new InvalidOperationException($"Unsupported voice ledger migration schema {state.SchemaVersion}.");
            return state;
        }

        var highWatermark = await ledger.Find(Builders<XpLedgerEntry>.Filter.Empty).SortByDescending(x => x.Id).Project(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        state = new VoiceLedgerMigrationState
        {
            SourceHighWatermarkId = highWatermark,
            Phase = highWatermark == null ? VoiceLedgerMigrationPhase.Verifying : VoiceLedgerMigrationPhase.Copying,
            StartedAtUtc = now,
            UpdatedAtUtc = now
        };
        try
        {
            await states.InsertOneAsync(state, cancellationToken: cancellationToken);
            return state;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return await states.Find(x => x.Id == VoiceLedgerMigrationState.SingletonId).FirstAsync(cancellationToken);
        }
    }

    private async Task<bool> CopyBatchAsync(VoiceLedgerMigrationState state, CancellationToken cancellationToken)
    {
        if (state.SourceHighWatermarkId == null)
        {
            await SetPhaseAsync(VoiceLedgerMigrationPhase.Verifying, cancellationToken);
            return true;
        }

        var filter = CursorFilter(state.CopyCursorId, state.SourceHighWatermarkId);
        var batch = await ledger.Find(filter).SortBy(x => x.Id).Limit(migrationBatchSize).ToListAsync(cancellationToken);
        if (batch.Count == 0)
        {
            await SetPhaseAsync(VoiceLedgerMigrationPhase.Verifying, cancellationToken);
            return true;
        }

        long sourceDocuments = 0, sourceDayParts = 0, sourceSeconds = 0, copied = 0, skipped = 0, generatedSegments = 0;
        decimal sourceXp = 0;
        foreach (var entry in batch)
        {
            if (!TryPlan(entry, out var parts))
            {
                skipped++;
                continue;
            }

            foreach (var part in parts)
            {
                var segment = part.Segments[0];
                if (await activities.Find(x => x.GuildId == part.GuildId && x.UserId == part.UserId && x.DayStartUtc == part.DayStartUtc && x.ProjectionStatus == VoiceActivityProjectionStatus.Projecting).AnyAsync(cancellationToken))
                    throw new InvalidOperationException("Legacy migration is waiting for an active voice projection to finish.");
                await accumulator.AccrueAsync(new VoiceAccrualSlice(part.GuildId, part.UserId, segment.SessionId,
                    segment.StartsAtUtc, segment.EndsAtUtc, segment.ChannelId, segment.SeasonId, segment.EligibleSeconds,
                    segment.AwardedXp, segment.EffectiveXpPerMinute, segment.ChannelMultiplier, segment.AppliedServerBoosterMultiplier,
                    segment.SettingsRevision, part.UpdatedAtUtc, AlreadyProjected: true), cancellationToken);
            }
            sourceDocuments++;
            sourceDayParts += parts.Count;
            sourceSeconds += parts.Sum(x => x.TotalEligibleSeconds);
            sourceXp += parts.Sum(x => x.TotalAwardedXp);
            generatedSegments += parts.Sum(x => x.Segments.Count);
            copied++;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await states.UpdateOneAsync(x => x.Id == VoiceLedgerMigrationState.SingletonId && x.CopyCursorId == state.CopyCursorId,
            Builders<VoiceLedgerMigrationState>.Update
                .Set(x => x.CopyCursorId, batch[^1].Id)
                .Inc(x => x.SourceDocuments, sourceDocuments)
                .Inc(x => x.SourceDayParts, sourceDayParts)
                .Inc(x => x.SourceSeconds, sourceSeconds)
                .Inc(x => x.SourceXp, sourceXp)
                .Inc(x => x.CopiedDocuments, copied)
                .Inc(x => x.GeneratedDayDocuments, sourceDayParts)
                .Inc(x => x.GeneratedSegments, generatedSegments)
                .Inc(x => x.SkippedDocuments, skipped)
                .Set(x => x.UpdatedAtUtc, now), cancellationToken: cancellationToken);
        return true;
    }

    private async Task<bool> VerifyAsync(VoiceLedgerMigrationState state, CancellationToken cancellationToken)
    {
        var legacyParts = await activities.Find(new BsonDocument("segments.settings_revision", LegacySettingsRevision)).ToListAsync(cancellationToken);
        var legacySegments = legacyParts.SelectMany(x => x.Segments).Where(x => x.SettingsRevision == LegacySettingsRevision).ToArray();
        var targetParts = legacyParts.Sum(x => x.SessionCursors.LongCount(y => y.SessionId.StartsWith("lv_", StringComparison.Ordinal)));
        var targetSeconds = legacySegments.Sum(x => x.EligibleSeconds);
        var targetXp = legacySegments.Sum(x => x.AwardedXp);
        var sourceEntries = state.SourceHighWatermarkId == null ? [] : await ledger.Find(Builders<XpLedgerEntry>.Filter.Lte(x => x.Id, state.SourceHighWatermarkId)).ToListAsync(cancellationToken);
        var eligibleSource = sourceEntries.Where(IsEligible).ToArray();
        var sourceSeasons = ToSeasonTotals(eligibleSource.Where(x => x.SeasonId != null).GroupBy(x => x.SeasonId!, StringComparer.Ordinal).Select(x => (x.Key, Seconds: x.Sum(VoiceSeconds), Xp: x.Sum(y => y.Amount))));
        var targetSeasons = ToSeasonTotals(legacySegments.Where(x => x.SeasonId != null).GroupBy(x => x.SeasonId!, StringComparer.Ordinal).Select(x => (x.Key, Seconds: x.Sum(y => y.EligibleSeconds), Xp: x.Sum(y => y.AwardedXp))));
        var sourceMembers = MemberTotals(eligibleSource.Select(x => (x.GuildId, x.UserId, Seconds: VoiceSeconds(x), Xp: x.Amount)));
        var targetMembers = MemberTotals(legacyParts.SelectMany(x => x.Segments.Where(y => y.SettingsRevision == LegacySettingsRevision).Select(y => (x.GuildId, x.UserId, Seconds: y.EligibleSeconds, Xp: y.AwardedXp))));
        var sourceMemberSeasons = MemberSeasonTotals(eligibleSource.Where(x => x.SeasonId != null).Select(x => (x.GuildId, x.UserId, SeasonId: x.SeasonId!, Seconds: VoiceSeconds(x), Xp: x.Amount)));
        var targetMemberSeasons = MemberSeasonTotals(legacyParts.SelectMany(x => x.Segments.Where(y => y.SettingsRevision == LegacySettingsRevision && y.SeasonId != null).Select(y => (x.GuildId, x.UserId, SeasonId: y.SeasonId!, Seconds: y.EligibleSeconds, Xp: y.AwardedXp))));
        var groupedMatches = MemberParity(sourceMembers, targetMembers) && MemberSeasonParity(sourceMemberSeasons, targetMemberSeasons);
        var matches = state.SourceDayParts == targetParts && state.SourceSeconds == targetSeconds && state.SourceXp == targetXp && SeasonParity(sourceSeasons, targetSeasons) && groupedMatches;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var duration = Math.Max(0, (long)(now - state.StartedAtUtc).TotalMilliseconds);
        var fingerprint = ParityFingerprint(state.SourceDocuments, state.SourceDayParts, state.SourceSeconds, state.SourceXp, targetParts, targetSeconds, targetXp, sourceMembers, targetMembers, sourceMemberSeasons, targetMemberSeasons);
        var report = new VoiceLedgerMigrationParityReport
        {
            Matches = matches,
            GroupedParityVerified = true,
            SourceDocuments = state.SourceDocuments,
            SourceDayParts = state.SourceDayParts,
            SourceSeconds = state.SourceSeconds,
            SourceXp = state.SourceXp,
            TargetDayParts = targetParts,
            TargetSeconds = targetSeconds,
            TargetXp = targetXp,
            GeneratedDayDocuments = legacyParts.Count,
            GeneratedSegments = legacySegments.LongLength,
            MigrationDurationMilliseconds = duration,
            SourceSeasonTotals = sourceSeasons,
            TargetSeasonTotals = targetSeasons,
            SourceMemberTotals = sourceMembers,
            TargetMemberTotals = targetMembers,
            SourceMemberSeasonTotals = sourceMemberSeasons,
            TargetMemberSeasonTotals = targetMemberSeasons,
            Fingerprint = fingerprint,
            CheckedAtUtc = now
        };
        await states.UpdateOneAsync(x => x.Id == VoiceLedgerMigrationState.SingletonId,
            Builders<VoiceLedgerMigrationState>.Update.Set(x => x.Parity, report)
                .Set(x => x.Phase, matches ? VoiceLedgerMigrationPhase.AwaitingDeletionApproval : VoiceLedgerMigrationPhase.ParityFailed)
                .Set(x => x.MigrationDurationMilliseconds, duration)
                .Set(x => x.UpdatedAtUtc, now), cancellationToken: cancellationToken);
        return false;
    }

    private async Task<bool> DeleteBatchAsync(VoiceLedgerMigrationState state, CancellationToken cancellationToken)
    {
        if (state.DeletionApprovedAtUtc == null || state.Parity is not { Matches: true, GroupedParityVerified: true } || state.DeletionApprovalFingerprint != state.Parity.Fingerprint)
            throw new InvalidOperationException("Legacy voice deletion requires explicit approval of the current successful parity report.");
        if (state.SourceHighWatermarkId == null)
        {
            await CompleteAsync(cancellationToken);
            return false;
        }

        var batch = await ledger.Find(CursorFilter(state.DeletionCursorId, state.SourceHighWatermarkId))
            .SortBy(x => x.Id).Limit(options.DeleteBatchSize).ToListAsync(cancellationToken);
        if (batch.Count == 0)
        {
            await CompleteAsync(cancellationToken);
            return false;
        }

        var ids = batch.Where(IsEligible).Select(x => x.Id!).ToArray();
        long deleted = 0;
        if (ids.Length != 0)
            deleted = (await ledger.DeleteManyAsync(Builders<XpLedgerEntry>.Filter.In(x => x.Id, ids), cancellationToken)).DeletedCount;
        await states.UpdateOneAsync(x => x.Id == VoiceLedgerMigrationState.SingletonId && x.DeletionCursorId == state.DeletionCursorId,
            Builders<VoiceLedgerMigrationState>.Update.Set(x => x.DeletionCursorId, batch[^1].Id).Inc(x => x.DeletedDocuments, deleted)
                .Set(x => x.UpdatedAtUtc, timeProvider.GetUtcNow().UtcDateTime), cancellationToken: cancellationToken);
        return true;
    }

    internal static bool TryPlan(XpLedgerEntry entry, out IReadOnlyList<VoiceActivityDay> parts)
    {
        parts = [];
        if (!IsEligible(entry)) return false;
        var start = Utc(entry.PeriodStartsAtUtc!.Value);
        var end = Utc(entry.PeriodEndsAtUtc!.Value);
        var boundaries = new List<DateTime>();
        for (var day = start.Date.AddDays(1); day < end; day = day.AddDays(1)) boundaries.Add(day);
        var intervals = VoiceActivityAccumulator.Split(start, end, boundaries);
        var sessionId = LegacySessionId(entry.Id!);
        var remainingXp = entry.Amount;
        var totalTicks = end.Ticks - start.Ticks;
        var result = new List<VoiceActivityDay>(intervals.Count);
        for (var index = 0; index < intervals.Count; index++)
        {
            var interval = intervals[index];
            var xp = index == intervals.Count - 1 ? remainingXp : entry.Amount * (interval.EndsAtUtc.Ticks - interval.StartsAtUtc.Ticks) / totalTicks;
            remainingXp -= xp;
            var seconds = (long)(interval.EndsAtUtc - interval.StartsAtUtc).TotalSeconds;
            var boosterMultiplier = entry.AppliedServerBoosterMultiplier is > 0 ? entry.AppliedServerBoosterMultiplier.Value : 1m;
            var segment = new VoiceActivitySegment
            {
                SessionId = sessionId,
                StartsAtUtc = interval.StartsAtUtc,
                EndsAtUtc = interval.EndsAtUtc,
                ChannelId = entry.ChannelId!.Value,
                SeasonId = entry.SeasonId,
                EligibleSeconds = seconds,
                AwardedXp = xp,
                EffectiveXpPerMinute = seconds == 0 ? 0 : xp * 60m / seconds,
                ChannelMultiplier = 1m,
                AppliedServerBoosterMultiplier = boosterMultiplier > 1m ? boosterMultiplier : null,
                SettingsRevision = LegacySettingsRevision
            };
            result.Add(new VoiceActivityDay
            {
                GuildId = entry.GuildId,
                UserId = entry.UserId,
                DayStartUtc = interval.StartsAtUtc.Date,
                Part = 0,
                Revision = 1,
                SessionCursors = [new VoiceSessionCursor { SessionId = sessionId, ProcessedThroughUtc = interval.EndsAtUtc }],
                TotalEligibleSeconds = seconds,
                TotalAwardedXp = xp,
                Segments = [segment],
                SeasonTotals = [new VoiceSeasonTotal { SeasonId = entry.SeasonId, EligibleSeconds = seconds, AwardedXp = xp, ProjectedEligibleSeconds = seconds, ProjectedXp = xp }],
                ProjectionStatus = VoiceActivityProjectionStatus.Applied,
                ProjectionRevision = 1,
                ProjectedRevision = 1,
                ProjectedEligibleSeconds = seconds,
                ProjectedXp = xp,
                ProjectedSeasonTotals = [new VoiceSeasonTotal { SeasonId = entry.SeasonId, EligibleSeconds = seconds, AwardedXp = xp, ProjectedEligibleSeconds = seconds, ProjectedXp = xp }],
                ProjectedAtUtc = entry.ProjectedAtUtc,
                CreatedAtUtc = entry.CreatedAt,
                UpdatedAtUtc = entry.ProjectedAtUtc!.Value
            });
        }
        parts = result;
        return true;
    }

    internal static bool IsEligible(XpLedgerEntry entry) =>
        entry.Id != null && ObjectId.TryParse(entry.Id, out _) &&
        entry.Source == "voice" &&
        entry.ProjectionStatus == SeasonProjectionStatus.Applied && entry.ProjectedAtUtc != null &&
        XpLedgerSemantics.GetEffectiveKind(entry) == XpLedgerEntryKind.AutomaticGrant &&
        entry.ChannelId != null && entry.Amount >= 0 &&
        !entry.IsProjectionControl && !entry.CooldownDenied &&
        entry.PeriodStartsAtUtc != null && entry.PeriodEndsAtUtc > entry.PeriodStartsAtUtc;

    internal static bool IsCompressedVoiceAuthoritative(VoiceLedgerMigrationState? state)
    {
        if (state?.Parity is not { Matches: true, GroupedParityVerified: true } parity || string.IsNullOrWhiteSpace(parity.Fingerprint)) return false;
        if (state.Phase == VoiceLedgerMigrationPhase.AwaitingDeletionApproval) return true;
        return state.Phase is VoiceLedgerMigrationPhase.Deleting or VoiceLedgerMigrationPhase.Completed &&
               state.DeletionApprovalFingerprint == parity.Fingerprint;
    }

    internal static string LegacySessionId(string ledgerId)
    {
        var bytes = ObjectId.Parse(ledgerId).ToByteArray();
        return "lv_" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static long VoiceSeconds(XpLedgerEntry entry) => (long)(entry.PeriodEndsAtUtc!.Value - entry.PeriodStartsAtUtc!.Value).TotalSeconds;
    private static List<VoiceSeasonTotal> ToSeasonTotals(IEnumerable<(string SeasonId, long Seconds, decimal Xp)> values) => values.OrderBy(x => x.SeasonId, StringComparer.Ordinal)
        .Select(x => new VoiceSeasonTotal { SeasonId = x.SeasonId, EligibleSeconds = x.Seconds, AwardedXp = x.Xp }).ToList();
    private static bool SeasonParity(IReadOnlyList<VoiceSeasonTotal> source, IReadOnlyList<VoiceSeasonTotal> target) => source.Count == target.Count && source.Zip(target).All(x => x.First.SeasonId == x.Second.SeasonId && x.First.EligibleSeconds == x.Second.EligibleSeconds && x.First.AwardedXp == x.Second.AwardedXp);

    internal static List<VoiceLedgerMigrationMemberTotal> MemberTotals(IEnumerable<(ulong GuildId, ulong UserId, long Seconds, decimal Xp)> values) => values
        .GroupBy(x => (x.GuildId, x.UserId)).OrderBy(x => x.Key.GuildId).ThenBy(x => x.Key.UserId)
        .Select(x => new VoiceLedgerMigrationMemberTotal { GuildId = x.Key.GuildId, UserId = x.Key.UserId, EligibleSeconds = x.Sum(y => y.Seconds), AwardedXp = x.Sum(y => y.Xp) }).ToList();

    internal static List<VoiceLedgerMigrationMemberSeasonTotal> MemberSeasonTotals(IEnumerable<(ulong GuildId, ulong UserId, string SeasonId, long Seconds, decimal Xp)> values) => values
        .GroupBy(x => (x.GuildId, x.UserId, x.SeasonId)).OrderBy(x => x.Key.GuildId).ThenBy(x => x.Key.UserId).ThenBy(x => x.Key.SeasonId, StringComparer.Ordinal)
        .Select(x => new VoiceLedgerMigrationMemberSeasonTotal { GuildId = x.Key.GuildId, UserId = x.Key.UserId, SeasonId = x.Key.SeasonId, EligibleSeconds = x.Sum(y => y.Seconds), AwardedXp = x.Sum(y => y.Xp) }).ToList();

    internal static bool MemberParity(IReadOnlyList<VoiceLedgerMigrationMemberTotal> source, IReadOnlyList<VoiceLedgerMigrationMemberTotal> target) =>
        source.Count == target.Count && source.Zip(target).All(x => x.First.GuildId == x.Second.GuildId && x.First.UserId == x.Second.UserId && x.First.EligibleSeconds == x.Second.EligibleSeconds && x.First.AwardedXp == x.Second.AwardedXp);

    internal static bool MemberSeasonParity(IReadOnlyList<VoiceLedgerMigrationMemberSeasonTotal> source, IReadOnlyList<VoiceLedgerMigrationMemberSeasonTotal> target) =>
        source.Count == target.Count && source.Zip(target).All(x => x.First.GuildId == x.Second.GuildId && x.First.UserId == x.Second.UserId && x.First.SeasonId == x.Second.SeasonId && x.First.EligibleSeconds == x.Second.EligibleSeconds && x.First.AwardedXp == x.Second.AwardedXp);

    private static FilterDefinition<XpLedgerEntry> CursorFilter(string? afterId, string highWatermarkId)
    {
        var filters = new List<FilterDefinition<XpLedgerEntry>> { Builders<XpLedgerEntry>.Filter.Lte(x => x.Id, highWatermarkId) };
        if (afterId != null) filters.Add(Builders<XpLedgerEntry>.Filter.Gt(x => x.Id, afterId));
        return Builders<XpLedgerEntry>.Filter.And(filters);
    }

    private Task SetPhaseAsync(VoiceLedgerMigrationPhase phase, CancellationToken cancellationToken) => states.UpdateOneAsync(
        x => x.Id == VoiceLedgerMigrationState.SingletonId,
        Builders<VoiceLedgerMigrationState>.Update.Set(x => x.Phase, phase).Set(x => x.UpdatedAtUtc, timeProvider.GetUtcNow().UtcDateTime),
        cancellationToken: cancellationToken);

    private async Task<bool> HasRequiredIndexAsync(CancellationToken cancellationToken)
    {
        using var cursor = await activities.Indexes.ListAsync(cancellationToken);
        return (await cursor.ToListAsync(cancellationToken)).Any(x => x.TryGetValue("name", out var name) && name.IsString && name.AsString == RequiredActivityIndex);
    }

    private Task CompleteAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return states.UpdateOneAsync(x => x.Id == VoiceLedgerMigrationState.SingletonId,
            Builders<VoiceLedgerMigrationState>.Update.Set(x => x.Phase, VoiceLedgerMigrationPhase.Completed)
                .Set(x => x.CompletedAtUtc, now).Set(x => x.UpdatedAtUtc, now), cancellationToken: cancellationToken);
    }

    private async Task RecordFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var message = exception.GetBaseException().GetType().Name + ": " + exception.GetBaseException().Message;
        message = message[..Math.Min(message.Length, 500)];
        await states.UpdateOneAsync(x => x.Id == VoiceLedgerMigrationState.SingletonId,
            Builders<VoiceLedgerMigrationState>.Update.Inc(x => x.FailureCount, 1).Set(x => x.LastError, message)
                .Set(x => x.LastErrorAtUtc, now).Set(x => x.UpdatedAtUtc, now), cancellationToken: cancellationToken);
    }

    private static string ParityFingerprint(long documents, long parts, long seconds, decimal sourceXp, long targetParts, long targetSeconds, decimal targetXp,
        IEnumerable<VoiceLedgerMigrationMemberTotal> sourceMembers, IEnumerable<VoiceLedgerMigrationMemberTotal> targetMembers,
        IEnumerable<VoiceLedgerMigrationMemberSeasonTotal> sourceMemberSeasons, IEnumerable<VoiceLedgerMigrationMemberSeasonTotal> targetMemberSeasons)
    {
        var memberValues = sourceMembers.Concat(targetMembers).Select(x => FormattableString.Invariant($"{x.GuildId}:{x.UserId}:{x.EligibleSeconds}:{x.AwardedXp}"));
        var seasonValues = sourceMemberSeasons.Concat(targetMemberSeasons).Select(x => FormattableString.Invariant($"{x.GuildId}:{x.UserId}:{x.SeasonId}:{x.EligibleSeconds}:{x.AwardedXp}"));
        var value = FormattableString.Invariant($"v2|{documents}|{parts}|{seconds}|{sourceXp}|{targetParts}|{targetSeconds}|{targetXp}|{string.Join(';', memberValues)}|{string.Join(';', seasonValues)}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
    }

    private static VoiceLedgerMigrationOptions Validate(VoiceLedgerMigrationOptions value)
    {
        if (value.CopyBatchSize is < 1 or > 1000) throw new OptionsValidationException(nameof(VoiceLedgerMigrationOptions), typeof(VoiceLedgerMigrationOptions), ["CopyBatchSize must be between 1 and 1000 when configured."]);
        if (value.DeleteBatchSize is < 1 or > 500) throw new OptionsValidationException(nameof(VoiceLedgerMigrationOptions), typeof(VoiceLedgerMigrationOptions), ["DeleteBatchSize must be between 1 and 500."]);
        if (value.MaxRetries is < 1 or > 100 || value.RetryDelaySeconds < 1 || value.IdleDelaySeconds < 1) throw new OptionsValidationException(nameof(VoiceLedgerMigrationOptions), typeof(VoiceLedgerMigrationOptions), ["Retry settings must be positive and MaxRetries must not exceed 100."]);
        return value;
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
