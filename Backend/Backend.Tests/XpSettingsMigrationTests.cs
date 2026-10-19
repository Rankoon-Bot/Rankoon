using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Rankoon.Controllers;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class XpSettingsMigrationTests
{
    internal static BsonDocument LegacyAnnouncements() => new()
    {
        { "_id", ObjectId.GenerateNewId() }, { "guild_id", 1L }, { "enabled", true }, { "channel_id", 123L },
        { "notify_mentioned_user", false }, { "use_default_fallback", false }, { "fallback_locale", "de" },
        { "announce_manual_adjustments", true }, { "avoid_recent_templates_per_user", 7 }, { "revision", 9L },
        { "templates", new BsonArray { new BsonDocument {
            { "id", "old-template" }, { "name", "Belohnung" }, { "enabled", true }, { "weight", 3 },
            { "content", "old single variant" }, { "contents", new BsonArray { "Hallo {user.mention}", "Level {level}" } },
            { "minimum_level", 5 }, { "maximum_level", 40 }, { "every_nth_level", 5 },
            { "exact_levels", new BsonArray { 10, 20 } }, { "sources", new BsonArray { "voice" } }, { "reward_role_requirement", "Any" }
        } } }, { "retired_setting", new BsonDocument("keep", true) }
    };

    [Fact]
    public void Real_unversioned_BSON_uses_legacy_version_while_new_models_use_current_version()
    {
        var loaded = BsonSerializer.Deserialize<GuildLevelUpAnnouncementSettings>(LegacyAnnouncements());
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(2, new GuildLevelUpAnnouncementSettings().SchemaVersion);
        Assert.True(LevelUpAnnouncementsController.Migrate(loaded).Lifetime.Enabled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Legacy_profiles_preserve_channel_variants_preferences_and_conditions(int version)
    {
        var old = LegacyAnnouncements();
        if (version > 0) old["schema_version"] = version;
        var upgraded = XpSettingsStore.Upgrade(XpSettingsStore.AnnouncementCollection, old)!;
        var settings = BsonSerializer.Deserialize<GuildLevelUpAnnouncementSettings>(upgraded);
        Assert.Equal(2, settings.SchemaVersion);
        Assert.True(settings.Lifetime.Enabled); Assert.Equal(123UL, settings.Lifetime.ChannelId);
        Assert.False(settings.Lifetime.NotifyUser); Assert.False(settings.Lifetime.UseDefaultFallback);
        Assert.True(settings.Lifetime.AnnounceManualAdjustments); Assert.Equal("de", settings.Lifetime.FallbackLocale);
        Assert.Equal(7, settings.Lifetime.AvoidRecentMessagesPerUser); Assert.Equal(9, settings.Revision);
        Assert.False(settings.Season.Enabled);
        var group = Assert.Single(settings.Lifetime.LevelUp.Groups);
        Assert.Equal("Belohnung", group.Name); Assert.Equal(3, group.Weight);
        Assert.Equal(5, group.Conditions.MinimumLevel); Assert.Equal(40, group.Conditions.MaximumLevel);
        Assert.Equal(5, group.Conditions.EveryNthLevel); Assert.Equal(new[] { 10, 20 }, group.Conditions.ExactLevels);
        Assert.Equal(new[] { "voice" }, group.Conditions.Sources);
        Assert.Equal(new[] { "Hallo {user.mention}", "Level {level}" }, group.Messages.Select(x => x.Content));
        Assert.Single(settings.Lifetime.Rewards.Groups);
        Assert.Equal(4, settings.Lifetime.LevelUp.Groups.Concat(settings.Lifetime.Rewards.Groups).SelectMany(x => x.Messages).Select(x => x.Id).Distinct().Count());
        foreach (var element in old.Where(x => x.Name != "schema_version")) Assert.Equal(element.Value, upgraded[element.Name]);
        Assert.Null(XpSettingsStore.Upgrade(XpSettingsStore.AnnouncementCollection, upgraded));
    }

    [Fact]
    public void Existing_current_profiles_including_explicit_disabling_are_never_replaced_by_old_fields()
    {
        var current = LegacyAnnouncements();
        current["schema_version"] = 2;
        current["lifetime"] = new LevelAnnouncementProfile { Enabled = false, ChannelId = 456 }.ToBsonDocument();
        current["season"] = new LevelAnnouncementProfile { Enabled = true, ChannelId = 789 }.ToBsonDocument();
        Assert.Null(XpSettingsStore.Upgrade(XpSettingsStore.AnnouncementCollection, current));
    }

    [Fact]
    public void Disabled_legacy_profile_and_single_content_keep_their_meaning()
    {
        var old = LegacyAnnouncements(); old["enabled"] = false;
        old["templates"][0].AsBsonDocument.Remove("contents");
        var result = BsonSerializer.Deserialize<GuildLevelUpAnnouncementSettings>(XpSettingsStore.Upgrade(XpSettingsStore.AnnouncementCollection, old)!);
        Assert.False(result.Lifetime.Enabled);
        Assert.Equal("old single variant", Assert.Single(Assert.Single(result.Lifetime.LevelUp.Groups).Messages).Content);
    }

    [Fact]
    public void Voice_upgrade_preserves_all_original_XP_settings_roles_rates_and_unknown_values()
    {
        var original = new GuildXpSettings { GuildId = 1, Enabled = false, LevelUpChannelId = 20,
            LevelRoles = [new() { Level = 7, RoleId = 50, Description = "Keep me" }], ExcludedRoleIds = [99],
            Voice = new() { PointsPerMinute = 3.25m, MinimumSessionSeconds = 127, RequireMultipleHumans = false, ExcludeAfkChannel = false }
        }.ToBsonDocument();
        original["future_setting"] = 42; original["voice"]["RetiredFlag"] = true;
        var upgraded = XpSettingsStore.Upgrade(XpSettingsStore.XpCollection, original)!;
        foreach (var element in original.Where(x => x.Name != "voice")) Assert.Equal(element.Value, upgraded[element.Name]);
        var settings = BsonSerializer.Deserialize<GuildXpSettings>(upgraded);
        Assert.False(settings.Enabled); Assert.Equal(3.25m, settings.Voice.PointsPerMinute);
        Assert.Equal(127, settings.Voice.MinimumSessionSeconds); Assert.Equal(1, settings.Voice.Eligibility!.MinimumHumanParticipants);
        Assert.True(settings.Voice.Eligibility.AwardInAfkChannel);
        Assert.Equal(original["voice"]["RetiredFlag"], upgraded["voice"]["RetiredFlag"]);
        Assert.Null(XpSettingsStore.Upgrade(XpSettingsStore.XpCollection, upgraded));
    }

    [Theory]
    [InlineData(XpSettingsStore.XpCollection)]
    [InlineData(XpSettingsStore.AnnouncementCollection)]
    public void Unknown_future_versions_stop_instead_of_loading_defaults(string collection)
    {
        Assert.Throws<InvalidOperationException>(() => XpSettingsStore.Upgrade(collection, new BsonDocument("schema_version", 99)));
    }

    [Fact]
    public void Incomplete_API_payloads_cannot_silently_reset_reward_configuration()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GuildXpSettings>("{}"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GuildLevelUpAnnouncementSettings>("{}"));
        var complete = JsonSerializer.Serialize(new GuildXpSettings());
        Assert.NotNull(JsonSerializer.Deserialize<GuildXpSettings>(complete));
    }

    [Fact]
    public void Saving_preserves_unknown_nested_fields_but_honors_explicit_reward_removal()
    {
        var old = new BsonDocument { { "level_roles", new BsonArray { new BsonDocument { { "RoleId", 1 }, { "Level", 3 }, { "future", true } } } },
            { "voice", new BsonDocument { { "PointsPerMinute", 10 }, { "future", 9 } } } };
        var submitted = new BsonDocument { { "level_roles", new BsonArray { new BsonDocument { { "RoleId", 1 }, { "Level", 4 } } } },
            { "voice", new BsonDocument("PointsPerMinute", 20) } };
        var merged = XpSettingsStore.Merge(old, submitted);
        Assert.True(merged["level_roles"][0]["future"].AsBoolean);
        Assert.Equal(4, merged["level_roles"][0]["Level"].AsInt32); Assert.Equal(9, merged["voice"]["future"].AsInt32);
        submitted["level_roles"] = new BsonArray();
        Assert.Empty(XpSettingsStore.Merge(old, submitted)["level_roles"].AsBsonArray);
    }

    [Fact]
    public void Legacy_priority_remains_effective_and_survives_an_editor_round_trip()
    {
        var old = LegacyAnnouncements();
        var template = old["templates"][0].AsBsonDocument;
        template["priority"] = 10;
        template["minimum_level"] = BsonNull.Value; template["maximum_level"] = BsonNull.Value;
        template["every_nth_level"] = BsonNull.Value; template["exact_levels"] = new BsonArray(); template["sources"] = new BsonArray();
        var upgraded = XpSettingsStore.Upgrade(XpSettingsStore.AnnouncementCollection, old)!;
        var settings = BsonSerializer.Deserialize<GuildLevelUpAnnouncementSettings>(upgraded);
        var group = Assert.Single(settings.Lifetime.LevelUp.Groups);
        Assert.Equal(10, group.LegacyPriority);
        settings.Lifetime.LevelUp.Groups.Add(new() { Name = "lower priority", Messages = [new() { Content = "Lower" }] });
        var selector = new LevelUpTemplateSelector(new LevelUpTemplateRenderer(), new ZeroRandom());
        var context = new LevelUpRenderContext("<@1>", "User", "User", 1, 1, 2, 100, 200, 100, "voice", null, "Guild", 1, 0, 0, null, []);
        Assert.Equal(group.Id, selector.Select(settings.Lifetime, LevelAnnouncementKind.LevelUp, context, [group.Id], [])!.Group.Id);
        var roundTrip = JsonSerializer.Deserialize<GuildLevelUpAnnouncementSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(0, roundTrip.Lifetime.LevelUp.Groups[0].LegacyPriority);
        var merged = XpSettingsStore.Merge(upgraded, roundTrip.ToBsonDocument()).AsBsonDocument;
        Assert.Equal(10, BsonSerializer.Deserialize<GuildLevelUpAnnouncementSettings>(merged).Lifetime.LevelUp.Groups[0].LegacyPriority);
    }

    private sealed class ZeroRandom : ILevelUpRandom { public int Next(int maximumExclusive) => 0; }
}

public sealed class XpSettingsMigrationPersistenceTests : IAsyncLifetime
{
    private readonly string databaseName = $"rankoon_settings_test_{Guid.NewGuid():N}";
    private MongoClient? client;
    private RankoonDbContext db = null!;
    private XpSettingsStore store = null!;

    public Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("RANKOON_SEASON_TEST_MONGO");
        if (connection == null) return Task.CompletedTask;
        client = new(connection); db = new(Options.Create(new MongoDbSettings { ConnectionString = connection, DatabaseName = databaseName }));
        store = new(db, TimeProvider.System); return Task.CompletedTask;
    }
    public Task DisposeAsync() => client?.DropDatabaseAsync(databaseName) ?? Task.CompletedTask;

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Parallel_restarts_preserve_original_backup_and_migrate_exactly_once()
    {
        var old = XpSettingsMigrationTests.LegacyAnnouncements();
        await db.RawSettings(XpSettingsStore.AnnouncementCollection).InsertOneAsync(old);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => new XpSettingsMigrationService(db, TimeProvider.System,
            NullLogger<XpSettingsMigrationService>.Instance).StartAsync(default)));
        var backup = Assert.Single(await db.RawSettings(XpSettingsStore.BackupCollection).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
        Assert.Equal(old, backup["original"]);
        var loaded = await store.LoadAnnouncementsAsync(1, default);
        Assert.True(loaded!.Lifetime.Enabled); Assert.Equal(123UL, loaded.Lifetime.ChannelId);
        Assert.Equal(9, loaded.Revision); Assert.Single(loaded.Lifetime.LevelUp.Groups);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Interrupted_migration_after_backup_resumes_without_duplicate_or_data_loss()
    {
        var old = XpSettingsMigrationTests.LegacyAnnouncements();
        await db.RawSettings(XpSettingsStore.AnnouncementCollection).InsertOneAsync(old);
        await store.BackupAsync(XpSettingsStore.AnnouncementCollection, old, "migration", default);
        await store.MigrateAsync(XpSettingsStore.AnnouncementCollection, old["_id"], default);
        Assert.Single(await db.RawSettings(XpSettingsStore.BackupCollection).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
        Assert.Equal(2, (await store.LoadAnnouncementsAsync(1, default))!.SchemaVersion);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Legacy_channel_is_carried_forward_without_enabling_seasons_or_replacing_a_saved_profile()
    {
        var xp = new GuildXpSettings { GuildId = 1, LevelUpChannelId = 123, LevelRoles = [new() { RoleId = 45, Level = 8 }] }.ToBsonDocument();
        xp["_id"] = ObjectId.GenerateNewId(); await db.RawSettings(XpSettingsStore.XpCollection).InsertOneAsync(xp);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.EnsureLegacyChannelAsync(xp, default)));
        var loaded = await store.LoadAnnouncementsAsync(1, default);
        Assert.True(loaded!.Lifetime.Enabled); Assert.False(loaded.Season.Enabled); Assert.Equal(123UL, loaded.Lifetime.ChannelId);
        Assert.Single(await db.GuildLevelUpAnnouncementSettings.Find(FilterDefinition<GuildLevelUpAnnouncementSettings>.Empty).ToListAsync());
        loaded.Lifetime.Enabled = false; loaded.Lifetime.ChannelId = 456;
        Assert.NotNull(await store.SaveAsync(XpSettingsStore.AnnouncementCollection, 1, 0, loaded.ToBsonDocument(), default));
        await store.EnsureLegacyChannelAsync(xp, default);
        loaded = await store.LoadAnnouncementsAsync(1, default);
        Assert.False(loaded!.Lifetime.Enabled); Assert.Equal(456UL, loaded.Lifetime.ChannelId);
        Assert.Equal(xp, await db.RawSettings(XpSettingsStore.XpCollection).Find(new BsonDocument("_id", xp["_id"])).SingleAsync());
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Concurrent_settings_saves_have_one_winner_and_stale_requests_cannot_erase_roles()
    {
        var settings = new GuildXpSettings { GuildId = 1, LevelRoles = [new() { RoleId = 25, Level = 4 }], LevelUpChannelId = 88 };
        var initial = await store.SaveAsync(XpSettingsStore.XpCollection, 1, 0, settings.ToBsonDocument(), default);
        Assert.NotNull(initial);
        settings.Revision = 1;
        var writes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.SaveAsync(XpSettingsStore.XpCollection, 1, 1, settings.ToBsonDocument(), default)));
        Assert.Single(writes, x => x != null);
        Assert.Null(await store.SaveAsync(XpSettingsStore.XpCollection, 1, 0, new GuildXpSettings { GuildId = 1 }.ToBsonDocument(), default));
        var saved = await db.GuildXpSettings.Find(x => x.GuildId == 1).SingleAsync();
        Assert.Equal(2, saved.Revision); Assert.Equal(25UL, Assert.Single(saved.LevelRoles).RoleId); Assert.Equal(88UL, saved.LevelUpChannelId);
        Assert.NotEmpty(await db.RawSettings(XpSettingsStore.BackupCollection).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Snapshot_compare_rejects_a_concurrent_write_even_without_revision_change()
    {
        var old = XpSettingsMigrationTests.LegacyAnnouncements();
        var documents = db.RawSettings(XpSettingsStore.AnnouncementCollection);
        await documents.InsertOneAsync(old);
        await documents.UpdateOneAsync(new BsonDocument("_id", old["_id"]), new BsonDocument("$set", new BsonDocument("channel_id", 456)));
        var result = await documents.ReplaceOneAsync(XpSettingsStore.SnapshotFilter(old), XpSettingsStore.Upgrade(XpSettingsStore.AnnouncementCollection, old)!);
        Assert.Equal(0, result.MatchedCount);
        Assert.Equal(456, (await documents.Find(new BsonDocument("_id", old["_id"])).SingleAsync())["channel_id"].ToInt32());
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Simultaneous_first_saves_create_only_one_settings_document_even_before_indexes_exist()
    {
        var writes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.SaveAsync(XpSettingsStore.XpCollection, 1, 0,
            new GuildXpSettings { GuildId = 1, LevelRoles = [new() { RoleId = 25, Level = 4 }] }.ToBsonDocument(), default)));
        Assert.Single(writes, x => x != null);
        Assert.Single(await db.GuildXpSettings.Find(FilterDefinition<GuildXpSettings>.Empty).ToListAsync());
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Failed_backup_stops_migration_before_any_original_settings_change()
    {
        var old = XpSettingsMigrationTests.LegacyAnnouncements();
        var documents = db.RawSettings(XpSettingsStore.AnnouncementCollection);
        await documents.InsertOneAsync(old);
        await documents.Database.CreateCollectionAsync(XpSettingsStore.BackupCollection, new CreateCollectionOptions<BsonDocument>
        {
            Validator = new BsonDocument("$jsonSchema", new BsonDocument { { "bsonType", "object" }, { "required", new BsonArray { "unavailable-backup-field" } } })
        });
        await Assert.ThrowsAsync<MongoWriteException>(() => store.MigrateAsync(XpSettingsStore.AnnouncementCollection, old["_id"], default));
        Assert.Equal(old, await documents.Find(new BsonDocument("_id", old["_id"])).SingleAsync());
    }
}
