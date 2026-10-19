using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Rankoon.Controllers;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Xp;

/// <summary>Runs to completion before any XP worker or HTTP endpoint can use settings.</summary>
public sealed class XpSettingsMigrationService(RankoonDbContext database, TimeProvider clock,
    ILogger<XpSettingsMigrationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var store = new XpSettingsStore(database, clock);
        foreach (var name in new[] { XpSettingsStore.XpCollection, XpSettingsStore.AnnouncementCollection })
        {
            using var cursor = await database.RawSettings(name).FindAsync(FilterDefinition<BsonDocument>.Empty,
                new FindOptions<BsonDocument> { BatchSize = 100 }, ct);
            while (await cursor.MoveNextAsync(ct))
                foreach (var document in cursor.Current)
                    await store.MigrateAsync(name, document["_id"], ct);
        }

        // Before the dedicated announcement editor existed, a configured XP channel
        // was the persisted instruction to announce level-ups. Never replace a profile.
        using var legacy = await database.RawSettings(XpSettingsStore.XpCollection).FindAsync(
            new BsonDocument("level_up_channel_id", new BsonDocument("$ne", BsonNull.Value)), cancellationToken: ct);
        while (await legacy.MoveNextAsync(ct))
            foreach (var xp in legacy.Current)
                await store.EnsureLegacyChannelAsync(xp, ct);
        logger.LogInformation("XP settings migrations completed; existing channels, rewards and announcement messages preserved");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class XpSettingsRevisionConflictException() : InvalidOperationException("XP settings changed since they were loaded. Reload the settings before saving.");

/// <summary>Lossless raw-document migrations and backed-up, optimistic settings writes.</summary>
internal sealed class XpSettingsStore(RankoonDbContext database, TimeProvider clock)
{
    internal const string XpCollection = "guild_xp_settings";
    internal const string AnnouncementCollection = "guild_level_up_announcement_settings";
    internal const string BackupCollection = "xp_settings_backups";

    internal static BsonDocument? Upgrade(string collection, BsonDocument original)
    {
        var currentVersion = collection == XpCollection ? 1 : 2;
        var version = original.GetValue("schema_version", 0).ToInt32();
        if (version > currentVersion)
            throw new InvalidOperationException($"{collection} uses unsupported settings schema {version}; refusing to overwrite it.");
        var updated = original.DeepClone().AsBsonDocument;
        if (collection == XpCollection)
        {
            // Deserialize the historical shape, but only write the adapted voice policy.
            // Roles, rates, exclusions, IDs and unknown fields stay byte-for-byte intact.
            var settings = BsonSerializer.Deserialize<GuildXpSettings>(original);
            if (settings.Voice == null) throw new InvalidOperationException("Stored voice settings are null; refusing to replace them with defaults.");
            VoiceXpSettingsNormalizer.NormalizeLegacy(settings.Voice);
            if (settings.Voice.Eligibility == null) throw new InvalidOperationException("Stored voice eligibility cannot be migrated.");
            updated["voice"] = Merge(original.GetValue("voice", new BsonDocument()), settings.Voice.ToBsonDocument());
        }
        else
        {
            // Missing version was previously mistaken for v2 by the CLR initializer.
            // Detect the persisted shape, including documents stamped v2 without profiles.
            var hasLegacy = original.Contains("templates") || original.Contains("channel_id") || original.Contains("enabled");
            if (version < 2 || (!original.Contains("lifetime") && hasLegacy))
            {
                var old = BsonSerializer.Deserialize<GuildLevelUpAnnouncementSettings>(original);
                var migrated = LevelUpAnnouncementsController.Migrate(old);
                if (!original.Contains("lifetime")) updated["lifetime"] = migrated.Lifetime.ToBsonDocument();
                if (!original.Contains("season")) updated["season"] = migrated.Season.ToBsonDocument();
            }
            // An explicitly saved empty/disabled v2 profile is intentional. Do not
            // resurrect obsolete templates over an administrator's later choices.
            if (!updated.GetValue("lifetime", BsonNull.Value).IsBsonDocument || !updated.GetValue("season", BsonNull.Value).IsBsonDocument)
                throw new InvalidOperationException("Announcement profiles are incomplete; refusing to replace them with defaults.");
            _ = BsonSerializer.Deserialize<GuildLevelUpAnnouncementSettings>(updated);
        }
        updated["schema_version"] = currentVersion;
        return updated.Equals(original) ? null : updated;
    }

    internal async Task MigrateAsync(string collection, BsonValue id, CancellationToken ct)
    {
        var documents = database.RawSettings(collection);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var original = await documents.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
            if (original == null) return;
            var upgraded = Upgrade(collection, original);
            if (upgraded == null) return;
            await BackupAsync(collection, original, "migration", ct);
            // Compare the entire original, including missing fields and legacy revisions.
            // A competing migration or administrator save causes a fresh read, never an overwrite.
            var result = await documents.ReplaceOneAsync(SnapshotFilter(original), upgraded, cancellationToken: ct);
            if (result.MatchedCount == 1) return;
        }
        throw new InvalidOperationException($"Concurrent changes prevented migrating {collection}/{id}; startup stopped safely. Retry after the other instance finishes.");
    }

    internal async Task EnsureLegacyChannelAsync(BsonDocument xp, CancellationToken ct)
    {
        if (!xp.TryGetValue("level_up_channel_id", out var channel) || channel.IsBsonNull) return;
        var guild = xp["guild_id"];
        var documents = database.RawSettings(AnnouncementCollection);
        if (await documents.Find(new BsonDocument("guild_id", guild)).AnyAsync(ct)) return;
        await BackupAsync(XpCollection, xp, "legacy-announcement-channel", ct);
        var settings = new GuildLevelUpAnnouncementSettings
        {
            GuildId = BsonSerializer.Deserialize<GuildXpSettings>(xp).GuildId,
            Lifetime = new() { Enabled = true, ChannelId = BsonSerializer.Deserialize<GuildXpSettings>(xp).LevelUpChannelId, FallbackLocale = "de" }
        }.ToBsonDocument();
        // Deterministic ID also protects installations whose unique indexes are not ready yet.
        settings["_id"] = StableId($"{AnnouncementCollection}:{guild}");
        try
        {
            await documents.UpdateOneAsync(new BsonDocument("guild_id", guild),
                new BsonDocument("$setOnInsert", settings), new UpdateOptions { IsUpsert = true }, ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            if (!await documents.Find(new BsonDocument("guild_id", guild)).AnyAsync(ct)) throw;
        }
    }

    internal async Task<BsonDocument?> SaveAsync(string collection, ulong guildId, long expectedRevision, BsonDocument submitted, CancellationToken ct)
    {
        var documents = database.RawSettings(collection);
        // Use the model's BSON representation for unsigned snowflakes as well.
        var filter = new BsonDocument("guild_id", new GuildXpSettings { GuildId = guildId }.ToBsonDocument()["guild_id"]);
        var original = await documents.Find(filter).FirstOrDefaultAsync(ct);
        if (original != null)
        {
            await MigrateAsync(collection, original["_id"], ct);
            original = await documents.Find(filter).FirstOrDefaultAsync(ct);
        }
        if ((original?.GetValue("revision", 0).ToInt64() ?? 0) != expectedRevision || (original == null && expectedRevision != 0)) return null;
        submitted.Remove("_id");
        var updated = original == null ? submitted : Merge(original, submitted).AsBsonDocument;
        updated["revision"] = expectedRevision + 1;
        updated["schema_version"] = collection == XpCollection ? 1 : 2;
        if (original == null)
        {
            // A stable ID closes the creation race even before indexes exist.
            updated["_id"] = StableId($"{collection}:{filter["guild_id"]}");
            try
            {
                var inserted = await documents.UpdateOneAsync(filter, new BsonDocument("$setOnInsert", updated), new UpdateOptions { IsUpsert = true }, ct);
                return inserted.UpsertedId == null ? null : updated;
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { return null; }
        }
        await BackupAsync(collection, original, "settings-save", ct);
        var result = await documents.ReplaceOneAsync(SnapshotFilter(original), updated, cancellationToken: ct);
        return result.MatchedCount == 1 ? updated : null;
    }

    internal async Task BackupAsync(string collection, BsonDocument original, string reason, CancellationToken ct)
    {
        var digest = Convert.ToHexString(SHA256.HashData(original.ToBson()));
        var key = $"{collection}:{original["_id"]}:{digest}";
        var backup = new BsonDocument { { "_id", key }, { "collection", collection }, { "document_id", original["_id"] },
            { "reason", reason }, { "created_at_utc", clock.GetUtcNow().UtcDateTime }, { "original", original.DeepClone() } };
        try
        {
            await database.RawSettings(BackupCollection).UpdateOneAsync(new BsonDocument("_id", key),
                new BsonDocument("$setOnInsert", backup), new UpdateOptions { IsUpsert = true }, ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
    }

    internal static FilterDefinition<BsonDocument> SnapshotFilter(BsonDocument original) =>
        new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$$ROOT", new BsonDocument("$literal", original) }));

    private static ObjectId StableId(string value) => new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)).Take(12).ToArray());

    internal async Task<GuildLevelUpAnnouncementSettings?> LoadAnnouncementsAsync(ulong guildId, CancellationToken ct)
    {
        var filter = new BsonDocument("guild_id", new GuildXpSettings { GuildId = guildId }.ToBsonDocument()["guild_id"]);
        var documents = database.RawSettings(AnnouncementCollection);
        var original = await documents.Find(filter).FirstOrDefaultAsync(ct);
        if (original == null)
        {
            var xp = await database.RawSettings(XpCollection).Find(filter).FirstOrDefaultAsync(ct);
            if (xp != null) await EnsureLegacyChannelAsync(xp, ct);
        }
        else await MigrateAsync(AnnouncementCollection, original["_id"], ct);
        var loaded = await documents.Find(filter).FirstOrDefaultAsync(ct);
        return loaded == null ? null : BsonSerializer.Deserialize<GuildLevelUpAnnouncementSettings>(loaded);
    }

    // Preserve future/retired fields even when an older API client cannot round-trip them.
    internal static BsonValue Merge(BsonValue original, BsonValue submitted)
    {
        if (original.IsBsonDocument && submitted.IsBsonDocument)
        {
            var result = original.DeepClone().AsBsonDocument;
            foreach (var field in submitted.AsBsonDocument)
                result[field.Name] = result.TryGetValue(field.Name, out var old) ? Merge(old, field.Value) : field.Value.DeepClone();
            return result;
        }
        if (original.IsBsonArray && submitted.IsBsonArray)
        {
            return new BsonArray(submitted.AsBsonArray.Select(item =>
            {
                if (!item.IsBsonDocument) return item.DeepClone();
                var key = new[] { "_id", "id", "RoleId", "ChannelId", "MinimumBoostMonths" }.FirstOrDefault(item.AsBsonDocument.Contains);
                var old = key == null ? null : original.AsBsonArray.FirstOrDefault(x => x.IsBsonDocument && x.AsBsonDocument.GetValue(key, BsonNull.Value) == item[key]);
                return old == null ? item.DeepClone() : Merge(old, item);
            }));
        }
        return submitted.DeepClone();
    }
}
