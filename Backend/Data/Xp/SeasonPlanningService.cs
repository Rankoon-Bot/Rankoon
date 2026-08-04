using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Xp;

public sealed class SeasonPlanningConflictException : InvalidOperationException
{
    public SeasonPlanningConflictException() : base("The requested season periods conflict with existing seasons.") { }
}

public sealed record SeasonSetupResult(GuildSeasonSettings Settings, IReadOnlyList<GuildSeason> Seasons);

public sealed class SeasonPlanningService(RankoonDbContext database, ISeasonService seasonSettings, TimeProvider timeProvider)
{
    private readonly string ownerPrefix = $"setup-{Guid.NewGuid():N}";

    public IReadOnlyList<SeasonScheduleCandidate> PlanAdditional(GuildSeasonSettings settings, IReadOnlyCollection<GuildSeason> existing, int count, DateTime now) =>
        SeasonSchedulePlanner.GenerateAdditional(settings, existing, count, now);

    public async Task<IReadOnlyList<GuildSeason>> PlanExplicitAsync(GuildSeasonSettings settings, int count, CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(settings.GuildId, cancellationToken);
        var existing = await database.GuildSeasons.Find(x => x.GuildId == settings.GuildId).SortBy(x => x.Sequence).ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var candidates = PlanAdditional(settings, existing, count, now);
        if (candidates.Count != count) throw new SeasonPlanningConflictException();
        return await CreateAdditionalAsync(settings, candidates, now, cancellationToken);
    }

    public async Task<IReadOnlyList<GuildSeason>> CreateAdditionalAsync(GuildSeasonSettings settings, IReadOnlyList<SeasonScheduleCandidate> candidates, DateTime now, CancellationToken cancellationToken = default)
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

        var operation = await database.SeasonSetupOperations.Find(x => x.GuildId == settings.GuildId && x.OperationId == operationId).FirstOrDefaultAsync(cancellationToken);
        if (operation == null)
        {
            await seasonSettings.SaveSettingsAsync(settings, cancellationToken);
            var persisted = await seasonSettings.GetSettingsAsync(settings.GuildId, cancellationToken);
            var existing = await database.GuildSeasons.Find(x => x.GuildId == settings.GuildId).SortBy(x => x.Sequence).ToListAsync(cancellationToken);
            var candidates = PlanAdditional(persisted, existing, count, timeProvider.GetUtcNow().UtcDateTime);
            if (candidates.Count != count) throw new SeasonPlanningConflictException();
            operation = new SeasonSetupOperation
            {
                GuildId = settings.GuildId,
                OperationId = operationId,
                Candidates = candidates.Select(ToStoredCandidate).ToList()
            };
            try { await database.SeasonSetupOperations.InsertOneAsync(operation, cancellationToken: cancellationToken); }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                operation = await database.SeasonSetupOperations.Find(x => x.GuildId == settings.GuildId && x.OperationId == operationId).FirstAsync(cancellationToken);
            }
        }

        var savedSettings = await seasonSettings.GetSettingsAsync(settings.GuildId, cancellationToken);
        var planned = await CreateAdditionalAsync(savedSettings, operation.Candidates.Select(ToCandidate).ToList(), timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        var ids = planned.Where(x => x.Id != null).Select(x => x.Id!).ToList();
        await database.SeasonSetupOperations.UpdateOneAsync(x => x.Id == operation.Id, Builders<SeasonSetupOperation>.Update.Set(x => x.SeasonIds, ids), cancellationToken: cancellationToken);
        return new(savedSettings, planned);
    }

    private async Task<PlanningLease> AcquireLeaseAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var owner = $"{ownerPrefix}-{Guid.NewGuid():N}";
        var deadline = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(15);
        while (timeProvider.GetUtcNow().UtcDateTime < deadline)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var filter = Builders<SeasonPlanningLease>.Filter.Eq(x => x.GuildId, guildId) &
                (Builders<SeasonPlanningLease>.Filter.Lt(x => x.ExpiresAtUtc, now) | Builders<SeasonPlanningLease>.Filter.Eq(x => x.OwnerId, owner));
            var update = Builders<SeasonPlanningLease>.Update.Set(x => x.OwnerId, owner).Set(x => x.ExpiresAtUtc, now.AddMinutes(2));
            if ((await database.SeasonPlanningLeases.UpdateOneAsync(filter, update, cancellationToken: cancellationToken)).MatchedCount > 0) return new(database, guildId, owner, timeProvider);
            try
            {
                await database.SeasonPlanningLeases.InsertOneAsync(new SeasonPlanningLease { GuildId = guildId, OwnerId = owner, ExpiresAtUtc = now.AddMinutes(2) }, new InsertOneOptions(), cancellationToken);
                return new(database, guildId, owner, timeProvider);
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

    private sealed class PlanningLease(RankoonDbContext database, ulong guildId, string owner, TimeProvider timeProvider) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(database.SeasonPlanningLeases.UpdateOneAsync(x => x.GuildId == guildId && x.OwnerId == owner, Builders<SeasonPlanningLease>.Update.Set(x => x.ExpiresAtUtc, timeProvider.GetUtcNow().UtcDateTime), cancellationToken: default));
    }
}
