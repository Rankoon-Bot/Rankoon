using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Xp;

public interface ILevelTransitionService
{
    Task EnsureAsync(XpLedgerEntry ledger, LevelTransitionSnapshot snapshot, CancellationToken cancellationToken = default);
}

public sealed class LevelTransitionService(RankoonDbContext database, TimeProvider timeProvider) : ILevelTransitionService
{
    public async Task EnsureAsync(XpLedgerEntry ledger, LevelTransitionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot.NewLevel == snapshot.PreviousLevel || !XpLedgerSemantics.AffectsLifetime(ledger)) return;
        var key = $"level-transition:{ledger.GrantKey}:lifetime";
        var transition = new LevelTransitionEvent
        {
            EventKey = key, LedgerGrantKey = ledger.GrantKey, GuildId = ledger.GuildId, UserId = ledger.UserId, Source = ledger.Source,
            PreviousTotalXp = snapshot.PreviousTotalXp, NewTotalXp = snapshot.NewTotalXp, PreviousLevel = snapshot.PreviousLevel, NewLevel = snapshot.NewLevel,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime, NextAttemptAtUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        try { await database.LevelTransitionEvents.InsertOneAsync(transition, cancellationToken: cancellationToken); }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
    }
}
