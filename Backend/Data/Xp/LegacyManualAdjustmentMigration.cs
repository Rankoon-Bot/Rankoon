using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Xp;

/// <summary>Records the pre-ledger manual basis without booking it a second time or resetting a live balance.</summary>
internal sealed class LegacyManualAdjustmentMigration(RankoonDbContext database, IXpProjectionCoordinator coordinator, TimeProvider clock)
{
    internal async Task RunAsync(int batchSize, CancellationToken ct = default)
    {
        string? after = null;
        while (true)
        {
            var filter = Builders<MemberXp>.Filter.Exists(x => x.ManualAdjustment) & Builders<MemberXp>.Filter.Ne(x => x.ManualAdjustment, 0);
            if (after != null) filter &= Builders<MemberXp>.Filter.Gt(x => x.Id, after);
            var members = await database.MemberXp.Find(filter).SortBy(x => x.Id).Limit(batchSize).ToListAsync(ct);
            if (members.Count == 0) break;
            foreach (var member in members) await RecordAsync(member.GuildId, member.UserId, null, ct);
            after = members[^1].Id;
        }
        after = null;
        while (true)
        {
            var filter = Builders<SeasonMemberXp>.Filter.Exists(x => x.ManualAdjustment) & Builders<SeasonMemberXp>.Filter.Ne(x => x.ManualAdjustment, 0);
            if (after != null) filter &= Builders<SeasonMemberXp>.Filter.Gt(x => x.Id, after);
            var members = await database.SeasonMemberXp.Find(filter).SortBy(x => x.Id).Limit(batchSize).ToListAsync(ct);
            if (members.Count == 0) break;
            foreach (var member in members) await RecordAsync(member.GuildId, member.UserId, member.SeasonId, ct);
            after = members[^1].Id;
        }
    }

    private async Task RecordAsync(ulong guildId, ulong userId, string? seasonId, CancellationToken ct)
    {
        await using var lease = await coordinator.AcquireAsync(guildId, userId, seasonId, ct)
            ?? throw new InvalidOperationException("Legacy manual XP migration is waiting for an active XP operation. Retry startup maintenance.");
        var key = seasonId == null ? $"migration:manual-adjustment:v1:{guildId}:{userId}" : $"migration:season-manual-adjustment:v1:{seasonId}:{userId}";
        if (await database.XpLedger.Find(x => x.GrantKey == key).AnyAsync(ct)) return;

        // A modern adjustment is already its own evidence. In particular, a new
        // member's first manual adjustment must never become another migration grant.
        var manual = await database.XpLedger.Find(x => x.GuildId == guildId && x.UserId == userId &&
            (x.Kind == XpLedgerEntryKind.ManualAdjustment || x.Kind == XpLedgerEntryKind.ManualAdjustmentReversal || x.Kind == XpLedgerEntryKind.SystemMigration)).ToListAsync(ct);
        if (manual.Any(x => seasonId == null ? XpLedgerSemantics.AffectsLifetime(x) : x.SeasonId == seasonId && XpLedgerSemantics.AffectsSeason(x))) return;

        decimal amount; string name;
        if (seasonId == null)
        {
            var member = await database.MemberXp.Find(x => x.GuildId == guildId && x.UserId == userId).FirstOrDefaultAsync(ct);
            if (member == null) return;
            amount = member.ManualAdjustment; name = member.DisplayName;
        }
        else
        {
            var member = await database.SeasonMemberXp.Find(x => x.SeasonId == seasonId && x.UserId == userId).FirstOrDefaultAsync(ct);
            if (member == null) return;
            amount = member.ManualAdjustment; name = member.DisplayName;
        }
        if (amount == 0 || !await lease.RenewAsync(ct)) return;
        var now = clock.GetUtcNow().UtcDateTime;
        var entry = new XpLedgerEntry
        {
            GrantKey = key, GuildId = guildId, UserId = userId, DisplayName = name, Amount = amount,
            Source = seasonId == null ? "legacy_manual_adjustment" : "legacy_season_manual_adjustment",
            Kind = XpLedgerEntryKind.SystemMigration, Scope = seasonId == null ? XpLedgerScope.LifetimeOnly : XpLedgerScope.SeasonOnly, SeasonId = seasonId,
            Reason = "Recorded existing legacy manual XP; balance unchanged", CreatedAt = now, OccurredAtUtc = now,
            ProjectionStatus = SeasonProjectionStatus.Applied, ProjectedAtUtc = now
        };
        try { await database.XpLedger.InsertOneAsync(entry, cancellationToken: ct); }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
    }
}
