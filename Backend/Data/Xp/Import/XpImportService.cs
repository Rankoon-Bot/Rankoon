using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Xp.Import;

public interface IXpImportService
{
    Task<XpImportResult> ImportAsync(ulong guildId, System.Text.Json.JsonElement payload, CancellationToken cancellationToken = default);
}

public sealed class XpImportService(
    XpImportParser parser,
    RankoonDbContext database,
    GuildMembershipService memberships,
    LevelRoleService levelRoles,
    ILeaderboardRealtimePublisher realtime,
    TimeProvider timeProvider,
    ILogger<XpImportService> logger) : IXpImportService
{
    private const int ChunkSize = 500;

    public async Task<XpImportResult> ImportAsync(ulong guildId, System.Text.Json.JsonElement payload, CancellationToken cancellationToken = default)
    {
        var parsed = parser.Parse(payload, guildId);
        var userIds = parsed.Members.Select(member => member.UserId).ToArray();
        var preferences = await database.MemberLeaderboardPreferences
            .Find(Builders<MemberLeaderboardPreference>.Filter.Eq(x => x.GuildId, guildId) &
                Builders<MemberLeaderboardPreference>.Filter.In(x => x.UserId, userIds))
            .ToListAsync(cancellationToken);
        var visibility = preferences.ToDictionary(preference => preference.UserId, preference => preference.PublicVisible);
        var updatedAt = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var chunk in parsed.Members.Chunk(ChunkSize))
        {
            var writes = chunk.Select(member => CreateUpdate(guildId, member, visibility.GetValueOrDefault(member.UserId, true), updatedAt)).ToArray();
            await database.MemberXp.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken);
        }

        await Parallel.ForEachAsync(parsed.Members, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        }, async (member, token) =>
        {
            try
            {
                await levelRoles.SynchronizeAsync(guildId, member.UserId, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Unable to synchronize level roles for imported user {UserId} in guild {GuildId}", member.UserId, guildId);
            }
        });

        memberships.QueueGuild(guildId);
        await realtime.PublishGuildAsync(guildId, cancellationToken);
        return new(parsed.Format, parsed.Members.Count, parsed.SkippedInvalid, parsed.SkippedForeignGuild, parsed.DuplicateUsers);
    }

    internal static UpdateOneModel<MemberXp> CreateUpdate(ulong guildId, XpImportMember member, bool publicVisible, DateTime updatedAt)
    {
        // The historical imported_mee6_xp field now stores the idempotent external import basis for every supported format.
        var set = new BsonDocument
        {
            { "guild_id", new BsonDocument("$ifNull", new BsonArray { "$guild_id", Decimal(guildId) }) },
            { "user_id", new BsonDocument("$ifNull", new BsonArray { "$user_id", Decimal(member.UserId) }) },
            { "display_name", member.DisplayName },
            { "normalized_display_name", XpService.NormalizeName(member.DisplayName) },
            { "imported_mee6_xp", Decimal(member.ImportedXp) },
            { "message_count", member.MessageCount },
            { "updated_at", updatedAt },
            { "is_current_member", new BsonDocument("$ifNull", new BsonArray { "$is_current_member", false }) },
            { "public_leaderboard_visible", new BsonDocument("$ifNull", new BsonArray { "$public_leaderboard_visible", publicVisible }) }
        };
        if (member.VoiceSeconds is { } voiceSeconds) set["voice_seconds"] = Decimal(voiceSeconds);

        var update = new PipelineUpdateDefinition<MemberXp>(new BsonDocument[]
        {
            new("$set", set),
            new("$set", new BsonDocument("total_xp", new BsonDocument("$add", new BsonArray
            {
                new BsonDocument("$ifNull", new BsonArray { "$imported_mee6_xp", 0 }),
                new BsonDocument("$ifNull", new BsonArray { "$earned_xp", 0 }),
                new BsonDocument("$ifNull", new BsonArray { "$manual_adjustment", 0 })
            })))
        });
        return new(
            Builders<MemberXp>.Filter.Eq(x => x.GuildId, guildId) & Builders<MemberXp>.Filter.Eq(x => x.UserId, member.UserId),
            update)
        {
            IsUpsert = true
        };
    }

    private static BsonDecimal128 Decimal(decimal value) => new(value);
    private static BsonDecimal128 Decimal(ulong value) => new((decimal)value);
}
