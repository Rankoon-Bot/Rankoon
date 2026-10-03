using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Xp;

public sealed class SeasonPlanningConflictException : InvalidOperationException
{
    public SeasonPlanningConflictException() : base("The requested season periods conflict with existing seasons.") { }
}

public sealed record SeasonSetupResult(GuildSeasonSettings Settings, IReadOnlyList<GuildSeason> Seasons);
public interface ISeasonMutationLease : IAsyncDisposable
{
    CancellationToken CancellationToken { get; }
}

public sealed class SeasonPlanningService(RankoonDbContext database, ISeasonService seasonSettings, TimeProvider timeProvider)
{
    private readonly string ownerPrefix = $"setup-{Guid.NewGuid():N}";

    public IReadOnlyList<SeasonScheduleCandidate> PlanAdditional(GuildSeasonSettings settings, IReadOnlyCollection<GuildSeason> existing, int count, DateTime now) =>
        SeasonSchedulePlanner.GenerateAdditional(settings, existing, count, now);

    public async Task<IReadOnlyList<GuildSeason>> PlanExplicitAsync(GuildSeasonSettings settings, int count, CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(settings.GuildId, cancellationToken);
        cancellationToken = lease.CancellationToken;
        settings = await seasonSettings.GetSettingsAsync(settings.GuildId, cancellationToken);
        if (!settings.Enabled) throw new SeasonPlanningConflictException();
        return await PlanUnderLeaseAsync(settings, count, cancellationToken);
    }

    // Caller holds the shared guild mutation lease (also used by the coordinator and API).
    internal async Task<IReadOnlyList<GuildSeason>> PlanUnderLeaseAsync(GuildSeasonSettings settings, int count, CancellationToken cancellationToken)
    {
        var existing = await database.GuildSeasons.Find(x => x.GuildId == settings.GuildId).SortBy(x => x.Sequence).ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var candidates = PlanAdditional(settings, existing, count, now);
        if (candidates.Count != count) throw new SeasonPlanningConflictException();
        return await CreateAdditionalAsync(settings, candidates, now, cancellationToken);
    }

    private async Task<IReadOnlyList<GuildSeason>> CreateAdditionalAsync(GuildSeasonSettings settings, IReadOnlyList<SeasonScheduleCandidate> candidates, DateTime now, CancellationToken cancellationToken = default)
    {
        var result = new List<GuildSeason>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var existing = await database.GuildSeasons.Find(x => x.GuildId == settings.GuildId && x.Sequence == candidate.Sequence).FirstOrDefaultAsync(cancellationToken);
            if (existing != null)
            {
                EnsureMatches(existing, candidate);
                result.Add(existing);
                continue;
            }
            var season = new GuildSeason
            {
                Id = MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
                GuildId = settings.GuildId,
                Sequence = candidate.Sequence,
                Number = candidate.Number,
                NumberingEpoch = settings.NumberingEpoch,
                Name = candidate.Name,
                StartsAtUtc = candidate.StartsAtUtc,
                EndsAtUtc = candidate.EndsAtUtc,
                CreatedAtUtc = now,
                Status = SeasonStatus.Scheduled,
                ScheduleRevision = settings.Revision,
                ScheduleOccurrence = candidate.ScheduleOccurrence,
                AutomaticallyNamed = true,
                SettingsSnapshot = settings
            };
            try { await database.GuildSeasons.InsertOneAsync(season, cancellationToken: cancellationToken); }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { }

            var persisted = await database.GuildSeasons.Find(x => x.GuildId == settings.GuildId && x.Sequence == candidate.Sequence).FirstOrDefaultAsync(cancellationToken);
            if (persisted == null) throw new SeasonPlanningConflictException();
            EnsureMatches(persisted, candidate);
            result.Add(persisted);
        }
        return result;
    }

    public async Task<SeasonSetupResult> SetupAsync(GuildSeasonSettings settings, int count, string operationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId)) throw new ArgumentException("An operation id is required.", nameof(operationId));
        await using var lease = await AcquireLeaseAsync(settings.GuildId, cancellationToken);
        cancellationToken = lease.CancellationToken;
        var requestJson = System.Text.Json.JsonSerializer.Serialize(new { Settings = settings, Count = count });
        var operation = await database.SeasonSetupOperations.Find(x => x.GuildId == settings.GuildId && x.OperationId == operationId).FirstOrDefaultAsync(cancellationToken);
        if (operation?.RequestJson != null && operation.RequestJson != requestJson) throw new SeasonPlanningConflictException();
        if (operation?.Completed == true)
        {
            var result = await database.GuildSeasons.Find(x => x.GuildId == settings.GuildId && operation.SeasonIds.Contains(x.Id!)).ToListAsync(cancellationToken);
            return new(await seasonSettings.GetSettingsAsync(settings.GuildId, cancellationToken), result);
        }
        if (operation == null)
        {
            var persisted = await seasonSettings.GetSettingsAsync(settings.GuildId, cancellationToken);
            if (settings.Revision != persisted.Revision) throw new SeasonPlanningConflictException();
            settings.NumberingEpoch = persisted.NumberingEpoch;
            settings.NextSequenceAfterDeletion = persisted.NextSequenceAfterDeletion;
            settings.NextSeasonStartUtc = persisted.NextSeasonStartUtc;
            settings.Revision = persisted.Revision + 1;
            var existing = await database.GuildSeasons.Find(x => x.GuildId == settings.GuildId).SortBy(x => x.Sequence).ToListAsync(cancellationToken);
            var candidates = PlanAdditional(settings, existing, count, timeProvider.GetUtcNow().UtcDateTime);
            if (candidates.Count != count) throw new SeasonPlanningConflictException();
            operation = new SeasonSetupOperation
            {
                GuildId = settings.GuildId,
                OperationId = operationId,
                Settings = settings,
                RequestJson = requestJson,
                Candidates = candidates.Select(ToStoredCandidate).ToList()
            };
            try { await database.SeasonSetupOperations.InsertOneAsync(operation, cancellationToken: cancellationToken); }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                operation = await database.SeasonSetupOperations.Find(x => x.GuildId == settings.GuildId && x.OperationId == operationId).FirstAsync(cancellationToken);
            }
        }

        var snapshot = operation.Settings ?? await seasonSettings.GetSettingsAsync(settings.GuildId, cancellationToken);
        var planned = await CompleteSetupUnderLeaseAsync(operation, snapshot, cancellationToken);
        var savedSettings = await seasonSettings.GetSettingsAsync(settings.GuildId, cancellationToken);
        return new(savedSettings, planned);
    }

    private async Task<IReadOnlyList<GuildSeason>> CompleteSetupUnderLeaseAsync(SeasonSetupOperation operation, GuildSeasonSettings snapshot, CancellationToken cancellationToken)
    {
        var planned = await CreateAdditionalAsync(snapshot, operation.Candidates.Select(ToCandidate).ToList(), timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        // Enable only after all instances exist. A crash after saving must not increment revision again.
        var current = await seasonSettings.GetSettingsAsync(snapshot.GuildId, cancellationToken);
        if (current.Revision < snapshot.Revision) await seasonSettings.SaveSettingsAsync(snapshot, cancellationToken);
        var ids = planned.Select(x => x.Id!).ToList();
        await database.SeasonSetupOperations.UpdateOneAsync(x => x.Id == operation.Id,
            Builders<SeasonSetupOperation>.Update.Set(x => x.SeasonIds, ids).Set(x => x.Completed, true), cancellationToken: cancellationToken);
        return planned;
    }

    private async Task<ISeasonMutationLease> RecoverUnderLeaseAsync(PlanningLease lease, ulong guildId, CancellationToken cancellationToken)
    {
        cancellationToken = lease.CancellationToken;
        try
        {
            var unfinished = await database.SeasonSetupOperations.Find(x => x.GuildId == guildId && !x.Completed).ToListAsync(cancellationToken);
            foreach (var operation in unfinished)
            {
                // Legacy operations already stored their completed IDs but had no Completed flag.
                if (operation.Settings == null && operation.SeasonIds.Count > 0)
                    await database.SeasonSetupOperations.UpdateOneAsync(x => x.Id == operation.Id, Builders<SeasonSetupOperation>.Update.Set(x => x.Completed, true), cancellationToken: cancellationToken);
                else await CompleteSetupUnderLeaseAsync(operation, operation.Settings ?? await seasonSettings.GetSettingsAsync(guildId, cancellationToken), cancellationToken);
            }
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    public async Task<ISeasonMutationLease> AcquireLeaseAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var owner = $"{ownerPrefix}-{Guid.NewGuid():N}";
        var deadline = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(15);
        while (timeProvider.GetUtcNow().UtcDateTime < deadline)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var filter = Builders<SeasonPlanningLease>.Filter.Eq(x => x.GuildId, guildId) &
                (Builders<SeasonPlanningLease>.Filter.Lt(x => x.ExpiresAtUtc, now) | Builders<SeasonPlanningLease>.Filter.Eq(x => x.OwnerId, owner));
            var update = Builders<SeasonPlanningLease>.Update.Set(x => x.OwnerId, owner).Set(x => x.ExpiresAtUtc, now.AddMinutes(2));
            if ((await database.SeasonPlanningLeases.UpdateOneAsync(filter, update, cancellationToken: cancellationToken)).MatchedCount > 0)
                return await RecoverUnderLeaseAsync(new PlanningLease(database, guildId, owner, timeProvider, cancellationToken), guildId, cancellationToken);
            try
            {
                await database.SeasonPlanningLeases.InsertOneAsync(new SeasonPlanningLease { GuildId = guildId, OwnerId = owner, ExpiresAtUtc = now.AddMinutes(2) }, new InsertOneOptions(), cancellationToken);
                return await RecoverUnderLeaseAsync(new PlanningLease(database, guildId, owner, timeProvider, cancellationToken), guildId, cancellationToken);
            }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { await Task.Delay(50, cancellationToken); }
        }
        throw new SeasonPlanningConflictException();
    }

    private static SeasonSetupCandidate ToStoredCandidate(SeasonScheduleCandidate candidate) => new() { Sequence = candidate.Sequence, Number = candidate.Number, StartsAtUtc = candidate.StartsAtUtc, EndsAtUtc = candidate.EndsAtUtc, Name = candidate.Name, ScheduleOccurrence = candidate.ScheduleOccurrence };
    private static SeasonScheduleCandidate ToCandidate(SeasonSetupCandidate candidate) => new(candidate.Sequence, candidate.StartsAtUtc, candidate.EndsAtUtc, candidate.Name, candidate.ScheduleOccurrence, candidate.Number);
    private static void EnsureMatches(GuildSeason season, SeasonScheduleCandidate candidate)
    {
        if (season.StartsAtUtc != candidate.StartsAtUtc || season.EndsAtUtc != candidate.EndsAtUtc || season.Name != candidate.Name || season.Number != candidate.Number) throw new SeasonPlanningConflictException();
    }

    private sealed class PlanningLease : ISeasonMutationLease
    {
        private readonly CancellationTokenSource stop = new();
        private readonly CancellationTokenSource ownership;
        public CancellationToken CancellationToken => ownership.Token;
        private readonly RankoonDbContext database;
        private readonly ulong guildId;
        private readonly string owner;
        private readonly TimeProvider timeProvider;
        private readonly Task renewal;

        public PlanningLease(RankoonDbContext database, ulong guildId, string owner, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            this.database = database; this.guildId = guildId; this.owner = owner; this.timeProvider = timeProvider;
            ownership = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            renewal = RenewAsync();
        }

        private async Task RenewAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stop.Token);
                    var result = await database.SeasonPlanningLeases.UpdateOneAsync(x => x.GuildId == guildId && x.OwnerId == owner,
                        Builders<SeasonPlanningLease>.Update.Set(x => x.ExpiresAtUtc, timeProvider.GetUtcNow().UtcDateTime.AddMinutes(2)), cancellationToken: stop.Token);
                    if (result.MatchedCount == 0) throw new SeasonPlanningConflictException();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch
            {
                // Stop protected work immediately if renewal fails; another worker may later acquire it.
                await ownership.CancelAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            try { await renewal; }
            finally
            {
                stop.Dispose();
                ownership.Dispose();
                await database.SeasonPlanningLeases.UpdateOneAsync(x => x.GuildId == guildId && x.OwnerId == owner,
                    Builders<SeasonPlanningLease>.Update.Set(x => x.ExpiresAtUtc, timeProvider.GetUtcNow().UtcDateTime), cancellationToken: default);
            }
        }
    }
}
