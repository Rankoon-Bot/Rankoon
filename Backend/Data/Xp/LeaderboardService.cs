using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Discord;
using Rankoon.Data.Auth;

namespace Rankoon.Data.Xp;

public sealed record LeaderboardEntryDto(long Rank, string UserId, string DisplayName, decimal TotalXp, int Level, long MessageCount, long VoiceSeconds, bool IsCurrentUser);
public sealed record LeaderboardPageDto(string GuildName, string Alias, LeaderboardVisibility Visibility, IReadOnlyList<LeaderboardEntryDto> Items, string? NextCursor, bool HasMore, bool IsMember, bool? PublicVisible);

public sealed class LeaderboardService(RankoonDbContext database, DiscordShardedClient discord, GuildMembershipService memberships, IOptions<JwtSettings> jwtSettings)
{
    private sealed record Cursor(string Xp, string UserId, long Rank);
    private readonly byte[] cursorKey = Encoding.UTF8.GetBytes(jwtSettings.Value.SecretKey);

    public async Task<GuildLeaderboardSettings> GetOrCreateSettingsAsync(ulong guildId, string guildName, CancellationToken cancellationToken = default)
    {
        var existing = await database.GuildLeaderboardSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (existing != null) return existing;

        string baseAlias;
        try { baseAlias = NormalizeAlias(guildName); }
        catch (ArgumentException) { baseAlias = $"guild-{guildId}"; }
        for (var suffix = 0; suffix < 1000; suffix++)
        {
            var suffixText = suffix == 0 ? string.Empty : $"-{suffix + 1}";
            var alias = $"{baseAlias[..Math.Min(baseAlias.Length, 48 - suffixText.Length)].TrimEnd('-')}{suffixText}";
            var settings = new GuildLeaderboardSettings { GuildId = guildId, Alias = alias };
            try
            {
                await database.GuildLeaderboardSettings.InsertOneAsync(settings, cancellationToken: cancellationToken);
                memberships.QueueGuild(guildId);
                return settings;
            }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                existing = await database.GuildLeaderboardSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
                if (existing != null) return existing;
            }
        }

        throw new InvalidOperationException("Could not create a unique leaderboard alias.");
    }

    public async Task<GuildLeaderboardSettings?> FindSettingsAsync(string alias, CancellationToken cancellationToken = default)
    {
        try { return await database.GuildLeaderboardSettings.Find(x => x.Alias == NormalizeAlias(alias)).FirstOrDefaultAsync(cancellationToken); }
        catch (ArgumentException) { return null; }
    }

    public async Task<GuildLeaderboardSettings> SaveSettingsAsync(ulong guildId, string guildName, string alias, LeaderboardVisibility visibility, CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateSettingsAsync(guildId, guildName, cancellationToken);
        settings.Alias = NormalizeAlias(alias);
        settings.Visibility = visibility;
        settings.UpdatedAt = DateTime.UtcNow;
        await database.GuildLeaderboardSettings.ReplaceOneAsync(x => x.GuildId == guildId, settings, cancellationToken: cancellationToken);
        return settings;
    }

    public async Task<LeaderboardPageDto> GetPageAsync(GuildLeaderboardSettings settings, bool isMember, ulong? currentUserId, string? cursor, int take, bool aroundCurrentUser, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 10, 50);
        var filter = BaseFilter(settings.GuildId, isMember);
        IFindFluent<MemberXp, MemberXp> query = database.MemberXp.Find(filter).SortByDescending(x => x.TotalXp).ThenBy(x => x.UserId);
        List<MemberXp> members;
        var hasMore = false;
        long? firstRankHint = cursor == null && !aroundCurrentUser ? 1 : null;

        if (aroundCurrentUser && currentUserId != null)
        {
            var member = await database.MemberXp.Find(filter & Builders<MemberXp>.Filter.Eq(x => x.UserId, currentUserId.Value)).FirstOrDefaultAsync(cancellationToken);
            if (member != null)
            {
                var before = await database.MemberXp.Find(filter & AheadOf(member.TotalXp, member.UserId))
                    .SortBy(x => x.TotalXp).ThenByDescending(x => x.UserId).Limit(take / 3).ToListAsync(cancellationToken);
                before.Reverse();
                var remaining = take - before.Count - 1;
                var after = await database.MemberXp.Find(filter & Behind(member.TotalXp, member.UserId))
                    .SortByDescending(x => x.TotalXp).ThenBy(x => x.UserId).Limit(remaining + 1).ToListAsync(cancellationToken);
                hasMore = after.Count > remaining;
                if (hasMore) after.RemoveAt(after.Count - 1);
                members = [.. before, member, .. after];
                return await CreatePageAsync(settings, isMember, currentUserId, filter, members, hasMore, null, cancellationToken);
            }
        }
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (cursor.Length > 256) throw new FormatException("Invalid leaderboard cursor.");
            var decoded = DecodeCursor(cursor);
            if (!decimal.TryParse(decoded.Xp, NumberStyles.Number, CultureInfo.InvariantCulture, out var xp) || !ulong.TryParse(decoded.UserId, out var userId))
                throw new FormatException("Invalid leaderboard cursor.");
            filter &= Builders<MemberXp>.Filter.Or(
                Builders<MemberXp>.Filter.Lt(x => x.TotalXp, xp),
                Builders<MemberXp>.Filter.And(Builders<MemberXp>.Filter.Eq(x => x.TotalXp, xp), Builders<MemberXp>.Filter.Gt(x => x.UserId, userId)));
            query = database.MemberXp.Find(filter).SortByDescending(x => x.TotalXp).ThenBy(x => x.UserId);
            firstRankHint = decoded.Rank + 1;
        }

        members = await query.Limit(take + 1).ToListAsync(cancellationToken);
        hasMore = members.Count > take;
        if (hasMore) members.RemoveAt(members.Count - 1);
        return await CreatePageAsync(settings, isMember, currentUserId, BaseFilter(settings.GuildId, isMember), members, hasMore, firstRankHint, cancellationToken);
    }

    private async Task<LeaderboardPageDto> CreatePageAsync(GuildLeaderboardSettings settings, bool isMember, ulong? currentUserId, FilterDefinition<MemberXp> rankFilter, List<MemberXp> members, bool hasMore, long? firstRankHint, CancellationToken cancellationToken)
    {
        long firstRank = firstRankHint ?? 1;
        if (firstRankHint == null && members.Count > 0)
            firstRank += await database.MemberXp.CountDocumentsAsync(rankFilter & AheadOf(members[0].TotalXp, members[0].UserId), cancellationToken: cancellationToken);

        var items = members.Select((member, index) => new LeaderboardEntryDto(
            firstRank + index,
            member.UserId.ToString(),
            member.DisplayName,
            member.TotalXp,
            Mee6LevelCurve.GetLevel(member.TotalXp),
            member.MessageCount,
            member.VoiceSeconds,
            currentUserId == member.UserId)).ToList();
        var nextCursor = hasMore && members.Count > 0 ? EncodeCursor(members[^1], firstRank + members.Count - 1) : null;
        bool? publicVisible = null;
        if (currentUserId != null)
        {
            var member = await database.MemberXp.Find(x => x.GuildId == settings.GuildId && x.UserId == currentUserId.Value).FirstOrDefaultAsync(cancellationToken);
            var preference = member == null ? await database.MemberLeaderboardPreferences.Find(x => x.GuildId == settings.GuildId && x.UserId == currentUserId.Value).FirstOrDefaultAsync(cancellationToken) : null;
            publicVisible = member?.PublicLeaderboardVisible ?? preference?.PublicVisible ?? true;
        }

        return new LeaderboardPageDto(discord.GetGuild(settings.GuildId)?.Name ?? settings.Alias, settings.Alias, settings.Visibility, items, nextCursor, hasMore, isMember, publicVisible);
    }

    public async Task SetPublicVisibilityAsync(ulong guildId, ulong userId, bool visible, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await database.MemberXp.UpdateOneAsync(x => x.GuildId == guildId && x.UserId == userId,
            Builders<MemberXp>.Update.Set(x => x.PublicLeaderboardVisible, visible).Set(x => x.UpdatedAt, now), cancellationToken: cancellationToken);
        await database.MemberLeaderboardPreferences.UpdateOneAsync(
            x => x.GuildId == guildId && x.UserId == userId,
            Builders<MemberLeaderboardPreference>.Update
                .SetOnInsert(x => x.GuildId, guildId).SetOnInsert(x => x.UserId, userId)
                .Set(x => x.PublicVisible, visible).Set(x => x.UpdatedAt, now),
            new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    public static string NormalizeAlias(string value)
    {
        var normalized = value.Trim().ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        normalized = Regex.Replace(normalized, "[^a-z0-9]+", "-").Trim('-');
        if (normalized.Length > 48) normalized = normalized[..48].TrimEnd('-');
        if (normalized.Length < 3 || ReservedAliases.Contains(normalized)) throw new ArgumentException("Alias must contain 3 to 48 URL-safe characters.");
        return normalized;
    }

    private static readonly HashSet<string> ReservedAliases = new(StringComparer.OrdinalIgnoreCase) { "api", "auth", "login", "dashboard", "rankings", "server-selection", "server-config" };
    private static FilterDefinition<MemberXp> BaseFilter(ulong guildId, bool isMember) =>
        Builders<MemberXp>.Filter.Eq(x => x.GuildId, guildId) &
        Builders<MemberXp>.Filter.Eq(x => x.IsCurrentMember, true) &
        (isMember ? Builders<MemberXp>.Filter.Empty : Builders<MemberXp>.Filter.Eq(x => x.PublicLeaderboardVisible, true));
    private static FilterDefinition<MemberXp> AheadOf(decimal xp, ulong userId) => Builders<MemberXp>.Filter.Or(
        Builders<MemberXp>.Filter.Gt(x => x.TotalXp, xp),
        Builders<MemberXp>.Filter.And(Builders<MemberXp>.Filter.Eq(x => x.TotalXp, xp), Builders<MemberXp>.Filter.Lt(x => x.UserId, userId)));
    private static FilterDefinition<MemberXp> Behind(decimal xp, ulong userId) => Builders<MemberXp>.Filter.Or(
        Builders<MemberXp>.Filter.Lt(x => x.TotalXp, xp),
        Builders<MemberXp>.Filter.And(Builders<MemberXp>.Filter.Eq(x => x.TotalXp, xp), Builders<MemberXp>.Filter.Gt(x => x.UserId, userId)));
    private string EncodeCursor(MemberXp member, long rank)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Cursor(member.TotalXp.ToString(CultureInfo.InvariantCulture), member.UserId.ToString(), rank)));
        var signature = HMACSHA256.HashData(cursorKey, payload);
        return $"{Convert.ToBase64String(payload)}.{Convert.ToBase64String(signature)}";
    }
    private Cursor DecodeCursor(string cursor)
    {
        try
        {
            var parts = cursor.Split('.', 2);
            if (parts.Length != 2) throw new FormatException("Invalid leaderboard cursor.");
            var payload = Convert.FromBase64String(parts[0]);
            var signature = Convert.FromBase64String(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(cursorKey, payload))) throw new FormatException("Invalid leaderboard cursor.");
            return JsonSerializer.Deserialize<Cursor>(Encoding.UTF8.GetString(payload)) ?? throw new FormatException("Invalid leaderboard cursor.");
        }
        catch (Exception exception) when (exception is FormatException or JsonException) { throw new FormatException("Invalid leaderboard cursor.", exception); }
    }
}
