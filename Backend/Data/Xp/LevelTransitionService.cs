using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Xp;

public interface ILevelTransitionService
{
    Task EnsureAsync(XpLedgerEntry ledger, LevelTransitionSnapshot snapshot, CancellationToken cancellationToken = default);
    Task EnsureAsync(ulong guildId, ulong userId, LevelTransitionCause cause, LevelTransitionSnapshot snapshot, CancellationToken cancellationToken = default);
}

public sealed record LevelTransitionCause(string Source, string Key, decimal GainedXp, ulong? ChannelId = null, string? LedgerGrantKey = null, bool SuppressAnnouncement = false);

public sealed class LevelTransitionService(RankoonDbContext database, TimeProvider timeProvider) : ILevelTransitionService
{
    public async Task EnsureAsync(XpLedgerEntry ledger, LevelTransitionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot.NewLevel == snapshot.PreviousLevel || !XpLedgerSemantics.AffectsLifetime(ledger)) return;
        await EnsureAsync(ledger.GuildId, ledger.UserId,
            new(ledger.Source, ledger.GrantKey, ledger.Amount, ledger.ChannelId, ledger.GrantKey, XpLedgerSemantics.GetEffectiveKind(ledger) == XpLedgerEntryKind.SystemMigration), snapshot, cancellationToken);
    }

    public async Task EnsureAsync(ulong guildId, ulong userId, LevelTransitionCause cause, LevelTransitionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot.NewLevel == snapshot.PreviousLevel) return;
        var key = CreateEventKey(cause.Key);
        var transition = new LevelTransitionEvent
        {
            EventKey = key, LedgerGrantKey = cause.LedgerGrantKey, CauseKey = cause.Key, GuildId = guildId, UserId = userId, Source = cause.Source,
            SourceChannelId = cause.ChannelId, GainedXp = cause.GainedXp, SuppressAnnouncement = cause.SuppressAnnouncement,
            PreviousTotalXp = snapshot.PreviousTotalXp, NewTotalXp = snapshot.NewTotalXp, PreviousLevel = snapshot.PreviousLevel, NewLevel = snapshot.NewLevel,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime, NextAttemptAtUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        try { await database.LevelTransitionEvents.InsertOneAsync(transition, cancellationToken: cancellationToken); }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
    }

    internal static string CreateEventKey(string causeKey) => $"level-transition:{causeKey}:lifetime";
}
