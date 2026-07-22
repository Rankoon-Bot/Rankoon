using System.Security.Cryptography;
using System.Collections.Concurrent;
using Discord.WebSocket;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Xp;

namespace Rankoon.Data.Development;

public sealed record DevelopmentLeaderboardStatus(ulong GuildId, int MockUserCount, long XpEventCount, decimal TotalMockXp, string? LeaderboardAlias);
public sealed record DevelopmentXpEventResult(int Requested, int Granted, DevelopmentLeaderboardStatus Status);

public sealed class DevelopmentLeaderboardService(
    RankoonDbContext database,
    DiscordShardedClient discord,
    IXpService xp,
    LeaderboardService leaderboard,
    ILeaderboardRealtimePublisher realtime,
    TimeProvider timeProvider)
{
    private const string Source = "development_mock";
    private const ulong MinimumMockUserId = 8_000_000_000_000_000_000;
    private static readonly string[] FirstNames = ["Alex", "Mika", "Sam", "Robin", "Nico", "Lena", "Jules", "Kim", "Noah", "Mara", "Toni", "Finn"];
    private static readonly string[] Callsigns = ["Aurora", "Byte", "Comet", "Drift", "Echo", "Flux", "Glitch", "Halo", "Ion", "Jolt", "Kite", "Lumen"];
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> guildLocks = new();

    public async Task<DevelopmentLeaderboardStatus> GetStatusAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var members = await database.MemberXp.Find(x => x.GuildId == guildId && x.IsDevelopmentMock).ToListAsync(cancellationToken);
        var eventCount = await database.XpLedger.CountDocumentsAsync(x => x.GuildId == guildId && x.Source == Source && x.GrantKey.Contains(":event:"), cancellationToken: cancellationToken);
        var settings = await database.GuildLeaderboardSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        return new(guildId, members.Count, eventCount, members.Sum(member => member.TotalXp), settings?.Alias);
    }

    public async Task<DevelopmentLeaderboardStatus> GenerateAsync(ulong guildId, int count, CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(count));
        var guild = discord.GetGuild(guildId) ?? throw new KeyNotFoundException("Guild is unavailable.");
        var gate = guildLocks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await RemoveCoreAsync(guildId, cancellationToken);
            await leaderboard.GetOrCreateSettingsAsync(guildId, guild.Name, cancellationToken);
            for (var index = 0; index < count; index++)
            {
                var userId = await CreateUserIdAsync(guildId, cancellationToken);
                await database.DevelopmentMockMembers.InsertOneAsync(new DevelopmentMockMember { GuildId = guildId, UserId = userId, CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime }, cancellationToken: cancellationToken);
                var displayName = $"[Mock] {FirstNames[index % FirstNames.Length]} {Callsigns[(index / FirstNames.Length) % Callsigns.Length]} {index + 1:000}";
                var amount = RandomNumberGenerator.GetInt32(500, Math.Max(501, count * 1_500));
                await xp.GrantAsync(new(guildId, userId, displayName, Source, amount, $"dev-mock:{guildId}:{userId}:seed:{Guid.NewGuid():N}", timeProvider.GetUtcNow().UtcDateTime, SuppressReport: true), cancellationToken);
                await MarkMemberAsync(guildId, userId, cancellationToken);
            }
            return await GetStatusAsync(guildId, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<DevelopmentXpEventResult> TriggerEventsAsync(ulong guildId, int count, int minimumXp, int maximumXp, CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 200 || minimumXp < 1 || maximumXp > 100_000 || minimumXp > maximumXp) throw new ArgumentOutOfRangeException(nameof(count));
        var gate = guildLocks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var members = await database.MemberXp.Find(x => x.GuildId == guildId && x.IsDevelopmentMock && x.IsCurrentMember).ToListAsync(cancellationToken);
            if (members.Count == 0) throw new InvalidOperationException("No mock users exist for this guild.");
            var granted = 0;
            for (var index = 0; index < count; index++)
            {
                var member = members[RandomNumberGenerator.GetInt32(members.Count)];
                var amount = RandomNumberGenerator.GetInt32(minimumXp, maximumXp + 1);
                if (await xp.GrantAsync(new(guildId, member.UserId, member.DisplayName, Source, amount, $"dev-mock:{guildId}:{member.UserId}:event:{Guid.NewGuid():N}", timeProvider.GetUtcNow().UtcDateTime, SuppressReport: true), cancellationToken)) granted++;
            }
            return new(count, granted, await GetStatusAsync(guildId, cancellationToken));
        }
        finally { gate.Release(); }
    }

    public async Task<DevelopmentLeaderboardStatus> RemoveAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var gate = guildLocks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await RemoveCoreAsync(guildId, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task<DevelopmentLeaderboardStatus> RemoveCoreAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var registeredIds = await database.DevelopmentMockMembers.Find(x => x.GuildId == guildId).Project(x => x.UserId).ToListAsync(cancellationToken);
        var markedIds = await database.MemberXp.Find(x => x.GuildId == guildId && x.IsDevelopmentMock).Project(x => x.UserId).ToListAsync(cancellationToken);
        var userIds = registeredIds.Concat(markedIds).Distinct().ToList();
        if (userIds.Count == 0) return await GetStatusAsync(guildId, cancellationToken);
        var grants = await database.XpLedger.Find(x => x.GuildId == guildId && userIds.Contains(x.UserId) && x.Source == Source).ToListAsync(cancellationToken);
        foreach (var grant in grants.Where(entry => XpLedgerSemantics.GetEffectiveKind(entry) == XpLedgerEntryKind.AutomaticGrant))
            await ReverseAndWaitAsync(grant, cancellationToken);

        await database.SeasonMemberXp.DeleteManyAsync(x => x.GuildId == guildId && userIds.Contains(x.UserId), cancellationToken);
        await database.SeasonFinalStandings.DeleteManyAsync(x => x.GuildId == guildId && userIds.Contains(x.UserId), cancellationToken);
        await database.MemberLeaderboardPreferences.DeleteManyAsync(x => x.GuildId == guildId && userIds.Contains(x.UserId), cancellationToken);
        await database.MemberXp.DeleteManyAsync(x => x.GuildId == guildId && userIds.Contains(x.UserId), cancellationToken);
        await database.XpLedger.DeleteManyAsync(x => x.GuildId == guildId && userIds.Contains(x.UserId) && (x.Source == Source || x.Source == $"{Source}_reversal"), cancellationToken);
        await database.DevelopmentMockMembers.DeleteManyAsync(x => x.GuildId == guildId && userIds.Contains(x.UserId), cancellationToken);
        foreach (var userId in userIds) await realtime.PublishMemberAsync(guildId, userId, cancellationToken);
        return await GetStatusAsync(guildId, cancellationToken);
    }

    private async Task ReverseAndWaitAsync(XpLedgerEntry grant, CancellationToken cancellationToken)
    {
        var reversalKey = $"{grant.GrantKey}:cleanup";
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await xp.ReverseGrantAsync(grant.GrantKey, reversalKey, Source, cancellationToken);
            var reversal = await database.XpLedger.Find(x => x.GrantKey == reversalKey).FirstOrDefaultAsync(cancellationToken);
            if (reversal?.ProjectionStatus == SeasonProjectionStatus.Applied) return;
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, cancellationToken);
        }
        throw new InvalidOperationException("Mock XP cleanup could not complete its projections.");
    }

    private async Task MarkMemberAsync(ulong guildId, ulong userId, CancellationToken cancellationToken)
    {
        await database.MemberXp.UpdateOneAsync(x => x.GuildId == guildId && x.UserId == userId,
            Builders<MemberXp>.Update.Set(x => x.IsDevelopmentMock, true).Set(x => x.IsCurrentMember, true).Set(x => x.PublicLeaderboardVisible, true), cancellationToken: cancellationToken);
        await database.SeasonMemberXp.UpdateManyAsync(x => x.GuildId == guildId && x.UserId == userId,
            Builders<SeasonMemberXp>.Update.Set(x => x.IsDevelopmentMock, true).Set(x => x.IsCurrentMember, true).Set(x => x.PublicLeaderboardVisible, true), cancellationToken: cancellationToken);
    }

    private async Task<ulong> CreateUserIdAsync(ulong guildId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var bytes = RandomNumberGenerator.GetBytes(sizeof(ulong));
            var candidate = MinimumMockUserId + BitConverter.ToUInt64(bytes) % 1_000_000_000_000_000_000;
            if (!await database.MemberXp.Find(x => x.GuildId == guildId && x.UserId == candidate).AnyAsync(cancellationToken)) return candidate;
        }
    }
}
