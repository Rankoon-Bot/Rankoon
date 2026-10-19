using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Operations;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class XpIntegrityPersistenceTests : IAsyncLifetime
{
    private readonly string databaseName = $"rankoon_integrity_test_{Guid.NewGuid():N}";
    private MongoClient? client;
    private RankoonDbContext db = null!;
    private readonly Clock clock = new();
    private readonly Errors errors = new();
    private readonly XpIntegrityOptions options = new() { Enabled = true, CatchUpAnnouncements = true };

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("RANKOON_SEASON_TEST_MONGO");
        if (connection == null) return;
        client = new(connection);
        db = new(Options.Create(new MongoDbSettings { ConnectionString = connection, DatabaseName = databaseName }));
        await db.MemberXp.Indexes.CreateOneAsync(new CreateIndexModel<MemberXp>(Builders<MemberXp>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UserId), new() { Unique = true }));
        await db.XpLedger.Indexes.CreateOneAsync(new CreateIndexModel<XpLedgerEntry>(Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.GrantKey), new() { Unique = true }));
        await db.LevelTransitionEvents.Indexes.CreateOneAsync(new CreateIndexModel<LevelTransitionEvent>(Builders<LevelTransitionEvent>.IndexKeys.Ascending(x => x.EventKey), new() { Unique = true }));
        await db.SeasonMemberXp.Indexes.CreateOneAsync(new CreateIndexModel<SeasonMemberXp>(Builders<SeasonMemberXp>.IndexKeys.Ascending(x => x.SeasonId).Ascending(x => x.UserId), new() { Unique = true }));
        await db.GuildXpSettings.InsertOneAsync(new() { GuildId = 1 });
        await db.GuildLevelUpAnnouncementSettings.InsertOneAsync(new() { GuildId = 1, Lifetime = new() { Enabled = true, ChannelId = 3 }, Season = new() { Enabled = true, ChannelId = 3 } });
    }

    public Task DisposeAsync() => client?.DropDatabaseAsync(databaseName) ?? Task.CompletedTask;
    private XpIntegrityService Service() => new(db, new XpProjectionCoordinator(db, clock), new LevelTransitionService(db, clock), new Realtime(), errors,
        new WorkerHealthRegistry(clock), clock, Options.Create(options), NullLogger<XpIntegrityService>.Instance);

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Legacy_announcements_are_restored_and_catch_up_is_queued_without_dashboard_or_new_activity()
    {
        await Seed(2, amount: 30355.4m);
        await db.GuildLevelUpAnnouncementSettings.DeleteManyAsync(FilterDefinition<GuildLevelUpAnnouncementSettings>.Empty);
        await db.RawSettings(XpSettingsStore.AnnouncementCollection).InsertOneAsync(XpSettingsMigrationTests.LegacyAnnouncements());

        var result = await Service().CheckMemberAsync(1, 2, 20);

        Assert.Equal(1, result.Queued);
        var transition = Assert.Single(await Events());
        Assert.Equal(22, transition.NewLevel); Assert.Equal(LevelTransitionStatus.Pending, transition.Status);
        Assert.Equal("xp_reconciliation", transition.Source);
        var migrated = await db.GuildLevelUpAnnouncementSettings.Find(x => x.GuildId == 1).SingleAsync();
        Assert.Equal(2, migrated.SchemaVersion); Assert.True(migrated.Lifetime.Enabled);
        Assert.Equal(123UL, migrated.Lifetime.ChannelId); Assert.Single(migrated.Lifetime.LevelUp.Groups);
        Assert.Equal(0, (await Service().CheckMemberAsync(1, 2, 20)).Queued);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Missing_projection_and_level_event_are_repaired_once_even_after_restarts_and_concurrent_sweeps()
    {
        await Seed(2, stored: 0);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Service().CheckMemberAsync(1, 2, 20)));
        var member = await Member(2);
        Assert.Equal(100, member.EarnedXp); Assert.Equal(100, member.TotalXp);
        var first = Assert.Single(await Events());
        Assert.Equal(0, first.PreviousLevel); Assert.Equal(1, first.NewLevel);
        Assert.Equal(LevelTransitionStatus.Pending, first.Status);
        var again = await Service().CheckMemberAsync(1, 2, 20);
        Assert.Equal(0, again.Corrected); Assert.Equal(0, again.Queued);
        Assert.Single(await Events());
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Dead_letters_get_one_new_attempt_per_hour_without_resetting_attempt_history()
    {
        await Seed(2);
        var failed = Event(LevelTransitionStatus.DeadLetter); failed.DeliveryAttempts = 5;
        await db.LevelTransitionEvents.InsertOneAsync(failed);
        Assert.Equal(1, (await Service().CheckMemberAsync(1, 2, 20)).Queued);
        var retry = Assert.Single(await Events());
        Assert.Equal(LevelTransitionStatus.RetryScheduled, retry.Status); Assert.Equal(5, retry.DeliveryAttempts);
        await db.LevelTransitionEvents.UpdateOneAsync(x => x.Id == failed.Id, Builders<LevelTransitionEvent>.Update.Set(x => x.Status, LevelTransitionStatus.DeadLetter));
        Assert.Equal(0, (await Service().CheckMemberAsync(1, 2, 20)).Queued);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, (await Service().CheckMemberAsync(1, 2, 20)).Queued);
        Assert.Single(await Events());
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Delivered_suppressed_completed_and_already_pending_events_are_never_duplicated()
    {
        foreach (var status in new[] { LevelTransitionStatus.Delivered, LevelTransitionStatus.CompletedWithoutAnnouncement, LevelTransitionStatus.Pending, LevelTransitionStatus.Processing, LevelTransitionStatus.RetryScheduled })
        {
            var user = (ulong)(10 + (int)status);
            await Seed(user);
            var existing = Event(status); existing.UserId = user; existing.EventKey = status.ToString(); existing.SuppressAnnouncement = true;
            await db.LevelTransitionEvents.InsertOneAsync(existing);
            Assert.Equal(0, (await Service().CheckMemberAsync(1, user, 20)).Queued);
        }
        Assert.Equal(5, (await Events()).Count);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task A_failed_old_level_is_not_announced_after_a_newer_level_was_delivered()
    {
        await Seed(2, amount: 300);
        var failed = Event(LevelTransitionStatus.DeadLetter);
        await db.LevelTransitionEvents.InsertOneAsync(failed);
        var delivered = Event(LevelTransitionStatus.Delivered); delivered.EventKey = "higher"; delivered.NewLevel = 2; delivered.CreatedAtUtc = clock.GetUtcNow().UtcDateTime.AddSeconds(1);
        await db.LevelTransitionEvents.InsertOneAsync(delivered);
        Assert.Equal(0, (await Service().CheckMemberAsync(1, 2, 20)).Queued);
        var old = await db.LevelTransitionEvents.Find(x => x.Id == failed.Id).SingleAsync();
        Assert.Equal(LevelTransitionStatus.CompletedWithoutAnnouncement, old.Status);
        Assert.Equal("supersededByDeliveredLevel", old.LastErrorCode);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task A_persisted_discord_receipt_prevents_resending_even_if_status_is_corrupt()
    {
        await Seed(2);
        var existing = Event(LevelTransitionStatus.DeadLetter); existing.DiscordMessageId = 123;
        await db.LevelTransitionEvents.InsertOneAsync(existing);
        Assert.Equal(0, (await Service().CheckMemberAsync(1, 2, 20)).Queued);
        Assert.Equal(LevelTransitionStatus.DeadLetter, Assert.Single(await Events()).Status);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Per_guild_budget_and_paging_eventually_cover_every_current_real_member()
    {
        options.BatchSize = 2; options.MaxAnnouncementsPerGuildPerRun = 2;
        foreach (var user in Enumerable.Range(2, 5)) await Seed((ulong)user);
        await Seed(20); await Seed(21);
        await db.MemberXp.UpdateOneAsync(x => x.UserId == 20, Builders<MemberXp>.Update.Set(x => x.IsCurrentMember, false));
        await db.MemberXp.UpdateOneAsync(x => x.UserId == 21, Builders<MemberXp>.Update.Set(x => x.IsDevelopmentMock, true));
        var first = await Service().RunOnceAsync();
        Assert.Equal(5, first.Checked); Assert.Equal(2, first.Queued); Assert.Equal(0, first.Failed);
        Assert.Equal(2, (await Service().RunOnceAsync()).Queued);
        Assert.Equal(1, (await Service().RunOnceAsync()).Queued);
        Assert.Equal(0, (await Service().RunOnceAsync()).Queued);
        Assert.Equal(5, (await Events()).Count);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Persistent_delivery_failures_cannot_starve_later_members_after_restart()
    {
        options.BatchSize = 2; options.MaxAnnouncementsPerGuildPerRun = 2;
        foreach (var user in Enumerable.Range(2, 4)) await Seed((ulong)user);
        foreach (var user in new[] { 2UL, 3UL })
        {
            var failed = Event(LevelTransitionStatus.DeadLetter); failed.UserId = user; failed.EventKey = $"failed:{user}";
            await db.LevelTransitionEvents.InsertOneAsync(failed);
        }
        Assert.Equal(2, (await Service().RunOnceAsync()).Queued);
        await db.LevelTransitionEvents.UpdateManyAsync(x => x.Status == LevelTransitionStatus.RetryScheduled,
            Builders<LevelTransitionEvent>.Update.Set(x => x.Status, LevelTransitionStatus.DeadLetter));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, (await Service().RunOnceAsync()).Queued);
        Assert.Equal(2, await db.LevelTransitionEvents.CountDocumentsAsync(x => x.UserId >= 4));
        var cursor = await db.XpIntegritySweepCursors.Find(x => x.Id == "guild:1").SingleAsync();
        Assert.Equal(5UL, cursor.AfterUserId);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Import_and_manual_baselines_do_not_generate_historical_announcements()
    {
        await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 2, ImportedMee6Xp = 1000, ManualAdjustment = 100, TotalXp = 1100 });
        var manual = Ledger(2, 100); manual.Kind = XpLedgerEntryKind.ManualAdjustment;
        await db.XpLedger.InsertOneAsync(manual);
        Assert.Equal(0, (await Service().CheckMemberAsync(1, 2, 20)).Queued);
        Assert.Empty(await Events());
        Assert.Equal(1100, (await Member(2)).TotalXp);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Explicit_manual_announcement_setting_is_honored_but_migrations_stay_silent()
    {
        await db.GuildLevelUpAnnouncementSettings.UpdateOneAsync(x => x.GuildId == 1,
            Builders<GuildLevelUpAnnouncementSettings>.Update.Set(x => x.Lifetime.AnnounceManualAdjustments, true));
        foreach (var user in new[] { 2UL, 3UL })
            await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = user, ImportedMee6Xp = 100, ManualAdjustment = 1000, TotalXp = 1100 });
        var manual = Ledger(2, 1000); manual.Kind = XpLedgerEntryKind.ManualAdjustment;
        var migration = Ledger(3, 1000); migration.Kind = XpLedgerEntryKind.SystemMigration;
        await db.XpLedger.InsertManyAsync([manual, migration]);
        Assert.Equal(1, (await Service().CheckMemberAsync(1, 2, 20)).Queued);
        Assert.Equal(0, (await Service().CheckMemberAsync(1, 3, 20)).Queued);
        Assert.Equal(2UL, Assert.Single(await Events()).UserId);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Active_season_balances_and_missing_events_are_recovered_independently()
    {
        var season = new GuildSeason { GuildId = 1, Name = "Season", Status = SeasonStatus.Active };
        await db.GuildSeasons.InsertOneAsync(season);
        await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 2 });
        var grant = Ledger(2, 100); grant.SeasonId = season.Id;
        await db.XpLedger.InsertOneAsync(grant);
        var result = await Service().CheckMemberAsync(1, 2, 20);
        Assert.Equal(2, result.Corrected); Assert.Equal(2, result.Queued);
        Assert.Equal(100, (await db.SeasonMemberXp.Find(x => x.SeasonId == season.Id).SingleAsync()).TotalXp);
        Assert.Equal(2, (await Events()).Count);
        Assert.Equal(0, (await Service().CheckMemberAsync(1, 2, 20)).Queued);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Pending_projection_is_deferred_and_missing_history_is_reported_without_removing_xp()
    {
        await Seed(2);
        var pending = Ledger(2, 10); pending.ProjectionStatus = SeasonProjectionStatus.Pending;
        await db.XpLedger.InsertOneAsync(pending);
        Assert.True((await Service().CheckMemberAsync(1, 2, 20)).Deferred);
        Assert.Empty(await Events());
        await db.XpLedger.DeleteManyAsync(FilterDefinition<XpLedgerEntry>.Empty);
        Assert.True((await Service().CheckMemberAsync(1, 2, 20)).NeedsReview);
        Assert.Equal(100, (await Member(2)).TotalXp);
        var error = Assert.Single(errors.Records);
        var context = OperationalErrorSanitizer.RedactContext(error.Context);
        Assert.Equal("2", context["userId"]);
        Assert.Equal("lifetime", context["xpScope"]);
        Assert.Equal("100", context["xpStoredEarned"]);
        Assert.Equal("0", context["xpSourceEarned"]);
        Assert.Equal("-100", context["xpEarnedDifference"]);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Disabled_control_or_announcements_have_no_announcement_side_effects()
    {
        await Seed(2, stored: 0);
        options.Enabled = false;
        Assert.Equal(0, (await Service().RunOnceAsync()).Checked);
        Assert.Equal(0, (await Member(2)).TotalXp);
        options.Enabled = true; options.CatchUpAnnouncements = false;
        Assert.Equal(1, (await Service().RunOnceAsync()).Corrected);
        Assert.Empty(await Events());
        options.CatchUpAnnouncements = true;
        await db.GuildLevelUpAnnouncementSettings.UpdateOneAsync(x => x.GuildId == 1,
            Builders<GuildLevelUpAnnouncementSettings>.Update.Set(x => x.Lifetime.Enabled, false));
        Assert.Equal(0, (await Service().CheckMemberAsync(1, 2, 20)).Queued);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Pre_projection_ledger_documents_are_historical_awards_and_never_new_pending_grants()
    {
        await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 2, EarnedXp = 27, TotalXp = 27 });
        var legacy = Ledger(2, 27).ToBsonDocument();
        legacy.Remove("projection_status"); legacy.Remove("kind"); legacy.Remove("scope"); legacy.Remove("is_projection_control"); legacy.Remove("cooldown_denied");
        await db.RawSettings("xp_ledger").InsertOneAsync(legacy);
        var entry = await db.XpLedger.Find(x => x.UserId == 2).SingleAsync();
        Assert.Equal(SeasonProjectionStatus.Applied, entry.ProjectionStatus);
        Assert.Equal(SeasonProjectionStatus.Pending, new XpLedgerEntry().ProjectionStatus);
        Assert.False((await Service().CheckMemberAsync(1, 2, 0)).NeedsReview);
        Assert.Equal(27, (await Member(2)).TotalXp);
        await db.MemberXp.UpdateOneAsync(x => x.UserId == 2, Builders<MemberXp>.Update.Set(x => x.EarnedXp, 0).Set(x => x.TotalXp, 0));
        Assert.Equal(1, (await Service().CheckMemberAsync(1, 2, 0)).Corrected);
        Assert.Equal(27, (await Member(2)).TotalXp);
        var audit = new XpAuditService(db, null!, null!, null!, clock, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), null!);
        var filter = new XpAuditEntryFilter(null, null, null, null, null, null, SeasonProjectionStatus.Applied, null, null);
        Assert.Equal(SeasonProjectionStatus.Applied, Assert.Single((await audit.GetEntriesAsync(1, 2, filter)).Items).ProjectionStatus);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task A_recorded_negative_manual_adjustment_is_restored_after_a_legacy_startup_reset()
    {
        await Seed(2, amount: 12);
        var baseline = Ledger(2, 0); baseline.Kind = XpLedgerEntryKind.SystemMigration; baseline.Source = "legacy_manual_adjustment";
        baseline.GrantKey = "migration:manual-adjustment:v1:1:2"; baseline.Scope = XpLedgerScope.LifetimeOnly;
        var adjustment = Ledger(2, -12); adjustment.Kind = XpLedgerEntryKind.ManualAdjustment; adjustment.Source = "manual_adjustment";
        await db.XpLedger.InsertManyAsync([baseline, adjustment]);
        var result = await Service().CheckMemberAsync(1, 2, 0);
        Assert.False(result.NeedsReview); Assert.Equal(1, result.Corrected);
        Assert.Equal(-12, (await Member(2)).ManualAdjustment); Assert.Equal(0, (await Member(2)).TotalXp);
        Assert.Equal(0, (await Service().CheckMemberAsync(1, 2, 0)).Corrected);
        Assert.Empty(errors.Records);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Legacy_manual_migration_records_existing_balances_once_and_never_resets_modern_adjustments()
    {
        var migration = new LegacyManualAdjustmentMigration(db, new XpProjectionCoordinator(db, clock), clock);
        var season = new GuildSeason { GuildId = 1, Status = SeasonStatus.Active };
        await db.GuildSeasons.InsertOneAsync(season);
        await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 2, ManualAdjustment = 10, TotalXp = 10 });
        await db.SeasonMemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 2, SeasonId = season.Id!, StartingXp = 30, ManualAdjustment = -5, TotalXp = 25 });
        await migration.RunAsync(1);
        await migration.RunAsync(1);
        Assert.Equal(10, (await Member(2)).TotalXp); Assert.Equal(10, (await Member(2)).ManualAdjustment);
        var seasonMember = await db.SeasonMemberXp.Find(x => x.SeasonId == season.Id).SingleAsync();
        Assert.Equal(-5, seasonMember.ManualAdjustment); Assert.Equal(25, seasonMember.TotalXp);
        var baselines = await db.XpLedger.Find(x => x.Kind == XpLedgerEntryKind.SystemMigration).ToListAsync();
        Assert.Equal(2, baselines.Count); Assert.All(baselines, x => Assert.Equal(SeasonProjectionStatus.Applied, x.ProjectionStatus));
        Assert.Empty(await Events());

        var modern = Ledger(2, 5); modern.Kind = XpLedgerEntryKind.ManualAdjustment;
        await db.XpLedger.InsertOneAsync(modern);
        await db.MemberXp.UpdateOneAsync(x => x.UserId == 2, Builders<MemberXp>.Update.Set(x => x.ManualAdjustment, 15).Set(x => x.TotalXp, 15));
        await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 3, ManualAdjustment = 20, TotalXp = 20 });
        var newMemberAdjustment = Ledger(3, 20); newMemberAdjustment.Kind = XpLedgerEntryKind.ManualAdjustment;
        await db.XpLedger.InsertOneAsync(newMemberAdjustment);
        await migration.RunAsync(1);
        Assert.Equal(15, (await Member(2)).ManualAdjustment); Assert.Equal(15, (await Member(2)).TotalXp);
        Assert.Equal(20, (await Member(3)).ManualAdjustment); Assert.Equal(20, (await Member(3)).TotalXp);
        Assert.Equal(2, await db.XpLedger.CountDocumentsAsync(x => x.Kind == XpLedgerEntryKind.SystemMigration));
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task A_busy_manual_migration_leaves_the_balance_intact_and_can_be_retried()
    {
        await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 2, ManualAdjustment = 10, TotalXp = 10 });
        var coordinator = new XpProjectionCoordinator(db, clock);
        var migration = new LegacyManualAdjustmentMigration(db, coordinator, clock);
        await using (var busy = await coordinator.AcquireAsync(1, 2, null))
        {
            Assert.NotNull(busy);
            await Assert.ThrowsAsync<InvalidOperationException>(() => migration.RunAsync(1));
            Assert.Equal(10, (await Member(2)).TotalXp);
            Assert.Empty(await db.XpLedger.Find(FilterDefinition<XpLedgerEntry>.Empty).ToListAsync());
        }
        await migration.RunAsync(1);
        await migration.RunAsync(1);
        Assert.Equal(10, Assert.Single(await db.XpLedger.Find(FilterDefinition<XpLedgerEntry>.Empty).ToListAsync()).Amount);
        Assert.Equal(10, (await Member(2)).TotalXp);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Members_with_default_xp_settings_are_not_missed_by_the_sweep()
    {
        await db.GuildXpSettings.DeleteManyAsync(FilterDefinition<GuildXpSettings>.Empty);
        await Seed(2);
        var result = await Service().RunOnceAsync();
        Assert.Equal(1, result.Checked); Assert.Equal(1, result.Queued);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Compressed_voice_and_denied_grants_are_compared_using_projected_authoritative_amounts()
    {
        await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 2 });
        await db.VoiceActivities.InsertOneAsync(new()
        {
            GuildId = 1, UserId = 2, DayStartUtc = clock.GetUtcNow().UtcDateTime.Date, ProjectionStatus = VoiceActivityProjectionStatus.Applied,
            TotalAwardedXp = 100, ProjectedXp = 100, TotalEligibleSeconds = 600, ProjectedEligibleSeconds = 600
        });
        var denied = Ledger(2, 500); denied.CooldownDenied = true;
        await db.XpLedger.InsertOneAsync(denied);
        Assert.Equal(1, (await Service().CheckMemberAsync(1, 2, 20)).Corrected);
        var member = await Member(2);
        Assert.Equal(100, member.TotalXp); Assert.Equal(100, member.VoiceXp);
        Assert.Equal(1, Assert.Single(await Events()).NewLevel);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Voice_cutover_keeps_skipped_and_late_ledger_awards_and_counts_copied_awards_once()
    {
        const decimal expected = 175.625m;
        await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 2, EarnedXp = expected, VoiceXp = expected, TotalXp = expected });
        var copied = Ledger(2, 100); copied.Source = "voice";
        var skipped = Ledger(2, 25.125m); skipped.Source = "voice"; // No duration/channel: migration cannot copy this entry.
        await db.XpLedger.InsertManyAsync([copied, skipped]);
        await db.VoiceActivities.InsertOneAsync(new()
        {
            GuildId = 1, UserId = 2, ProjectionStatus = VoiceActivityProjectionStatus.Applied,
            TotalAwardedXp = 100, ProjectedXp = 100, TotalEligibleSeconds = 600, ProjectedEligibleSeconds = 600,
            Segments = [new() { SessionId = VoiceLedgerMigrationService.LegacySessionId(copied.Id!), SettingsRevision = VoiceLedgerMigrationService.LegacySettingsRevision, AwardedXp = 100, EligibleSeconds = 600 }]
        });
        await db.VoiceLedgerMigrationStates.InsertOneAsync(new()
        {
            SourceHighWatermarkId = skipped.Id, Phase = VoiceLedgerMigrationPhase.AwaitingDeletionApproval,
            Parity = new() { Matches = true, GroupedParityVerified = true, Fingerprint = "verified" }
        });
        var late = Ledger(2, 50.5m); late.Source = "voice"; late.ChannelId = 3;
        late.PeriodStartsAtUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(-1); late.PeriodEndsAtUtc = clock.GetUtcNow().UtcDateTime;
        late.ProjectedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.XpLedger.InsertOneAsync(late);

        var check = await Service().CheckMemberAsync(1, 2, 0);
        Assert.False(check.NeedsReview);
        Assert.Equal(expected, (await Member(2)).TotalXp);
        Assert.Empty(errors.Records);

        // Also restore a previously reduced projection from the same durable evidence.
        await db.MemberXp.UpdateOneAsync(x => x.UserId == 2, Builders<MemberXp>.Update
            .Set(x => x.EarnedXp, 100).Set(x => x.VoiceXp, 100).Set(x => x.TotalXp, 100));
        Assert.Equal(1, (await Service().CheckMemberAsync(1, 2, 0)).Corrected);
        Assert.Equal(expected, (await Member(2)).EarnedXp);
        Assert.Equal(expected, (await Member(2)).VoiceXp);
        Assert.Equal(expected, (await Member(2)).TotalXp);
        Assert.Equal(0, (await Service().CheckMemberAsync(1, 2, 0)).Corrected);

        var audit = new XpAuditService(db, null!, null!, null!, clock, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), null!);
        var filter = new XpAuditEntryFilter(null, null, null, null, null, null, null, null, null);
        var entries = await audit.GetEntriesAsync(1, 2, filter);
        Assert.Equal(2, entries.Items.Count);
        Assert.DoesNotContain(entries.Items, x => x.Id == copied.Id);
        var timeline = await audit.GetTimelineAsync(1, 2, filter with { Source = "voice" });
        Assert.Equal(expected, timeline.Items.Sum(x => x.TotalAmount));
        Assert.Single(timeline.Items, x => x.ItemType == XpAuditTimelineItemType.VoiceDay);
        Assert.Equal(2, timeline.Items.Count(x => x.ItemType == XpAuditTimelineItemType.Entry));

        // Approved deletion of the copied originals must not change the result.
        await db.XpLedger.DeleteOneAsync(x => x.Id == copied.Id);
        await db.VoiceLedgerMigrationStates.UpdateOneAsync(x => x.Id == VoiceLedgerMigrationState.SingletonId,
            Builders<VoiceLedgerMigrationState>.Update.Set(x => x.Phase, VoiceLedgerMigrationPhase.Completed).Set(x => x.DeletionApprovalFingerprint, "verified"));
        Assert.False((await Service().CheckMemberAsync(1, 2, 0)).NeedsReview);
        Assert.Equal(expected, (await Member(2)).TotalXp);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Active_season_recovers_uncopied_voice_awards_without_changing_the_starting_balance_or_closed_seasons()
    {
        var active = new GuildSeason { GuildId = 1, Name = "Current", Status = SeasonStatus.Active };
        var closed = new GuildSeason { GuildId = 1, Name = "Past", Status = SeasonStatus.Closed };
        await db.GuildSeasons.InsertManyAsync([active, closed]);
        await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 2, ImportedMee6Xp = 30, EarnedXp = 125.125m, VoiceXp = 125.125m, TotalXp = 155.125m });
        await db.SeasonMemberXp.InsertManyAsync([
            new() { GuildId = 1, UserId = 2, SeasonId = active.Id!, StartingXp = 20, EarnedXp = 100, VoiceXp = 100, TotalXp = 120 },
            new() { GuildId = 1, UserId = 2, SeasonId = closed.Id!, StartingXp = 9, EarnedXp = 10, TotalXp = 19 }
        ]);
        var copied = Ledger(2, 100); copied.Source = "voice"; copied.SeasonId = active.Id;
        var skipped = Ledger(2, 25.125m); skipped.Source = "voice"; skipped.SeasonId = active.Id;
        await db.XpLedger.InsertManyAsync([copied, skipped]);
        await db.VoiceActivities.InsertOneAsync(new()
        {
            GuildId = 1, UserId = 2, ProjectionStatus = VoiceActivityProjectionStatus.Applied,
            TotalAwardedXp = 100, ProjectedXp = 100, TotalEligibleSeconds = 600, ProjectedEligibleSeconds = 600,
            SeasonTotals = [new() { SeasonId = active.Id, AwardedXp = 100, EligibleSeconds = 600 }],
            ProjectedSeasonTotals = [new() { SeasonId = active.Id, AwardedXp = 100, EligibleSeconds = 600 }],
            Segments = [new() { SessionId = VoiceLedgerMigrationService.LegacySessionId(copied.Id!), SettingsRevision = VoiceLedgerMigrationService.LegacySettingsRevision, AwardedXp = 100, EligibleSeconds = 600, SeasonId = active.Id }]
        });
        await db.VoiceLedgerMigrationStates.InsertOneAsync(new()
        {
            Phase = VoiceLedgerMigrationPhase.AwaitingDeletionApproval,
            Parity = new() { Matches = true, GroupedParityVerified = true, Fingerprint = "verified" }
        });
        Assert.Equal(1, (await Service().CheckMemberAsync(1, 2, 0)).Corrected);
        var current = await db.SeasonMemberXp.Find(x => x.SeasonId == active.Id).SingleAsync();
        Assert.Equal(20, current.StartingXp); Assert.Equal(125.125m, current.EarnedXp); Assert.Equal(125.125m, current.VoiceXp); Assert.Equal(145.125m, current.TotalXp);
        Assert.Equal(19, (await db.SeasonMemberXp.Find(x => x.SeasonId == closed.Id).SingleAsync()).TotalXp);
        Assert.Equal(155.125m, (await Member(2)).TotalXp);
        Assert.Equal(0, (await Service().CheckMemberAsync(1, 2, 0)).Corrected);
        Assert.Empty(errors.Records);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Falsely_applied_voice_days_are_returned_to_the_projection_pipeline_without_double_awards()
    {
        await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 2, EarnedXp = 100, VoiceXp = 100, TotalXp = 100 });
        await db.VoiceActivities.InsertOneAsync(new()
        {
            GuildId = 1, UserId = 2, ProjectionStatus = VoiceActivityProjectionStatus.Applied, ProjectionRevision = 2, ProjectedRevision = 1,
            TotalAwardedXp = 200, ProjectedXp = 100, TotalEligibleSeconds = 1200, ProjectedEligibleSeconds = 600
        });
        var result = await Service().CheckMemberAsync(1, 2, 20);
        Assert.True(result.Deferred); Assert.Equal(1, result.Corrected);
        Assert.Equal(100, (await Member(2)).TotalXp);
        var day = await db.VoiceActivities.Find(FilterDefinition<VoiceActivityDay>.Empty).SingleAsync();
        Assert.Equal(VoiceActivityProjectionStatus.Pending, day.ProjectionStatus);
        var projection = new VoiceActivityProjectionService(db, new XpProjectionCoordinator(db, clock), new LevelTransitionService(db, clock), new Realtime(), clock, Options.Create(new VoiceActivityOptions()));
        await projection.ProjectAsync(day, "Member");
        await Service().CheckMemberAsync(1, 2, 20);
        Assert.Equal(200, (await Member(2)).TotalXp);
        await Service().CheckMemberAsync(1, 2, 20);
        Assert.Equal(200, (await Member(2)).TotalXp);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Error_recorder_outage_cannot_strand_a_failed_delivery_without_a_retry()
    {
        var resolver = LevelRoleReliabilityTests.InterfaceStub.Create<IGuildDiscordContextResolver>((_, _) => ValueTask.FromResult<GuildDiscordContext?>(null));
        var brokenErrors = LevelRoleReliabilityTests.InterfaceStub.Create<IOperationalErrorRecorder>((_, _) => throw new InvalidOperationException("Recorder unavailable"));
        var worker = new LevelProgressionWorker(db, resolver, new LevelRoleService(resolver, null!), null!, null!, null!, null!, null!, brokenErrors,
            new WorkerHealthRegistry(clock), clock, NullLogger<LevelProgressionWorker>.Instance);
        var entry = Event(LevelTransitionStatus.Pending);
        await db.LevelTransitionEvents.InsertOneAsync(entry);
        await worker.ProcessAsync(entry, default);
        var retry = Assert.Single(await Events());
        Assert.Equal(LevelTransitionStatus.RetryScheduled, retry.Status);
        Assert.Null(retry.LeaseOwner);
    }

    private async Task Seed(ulong user, decimal amount = 100, decimal? stored = null)
    {
        await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = user, EarnedXp = stored ?? amount, TotalXp = stored ?? amount });
        await db.XpLedger.InsertOneAsync(Ledger(user, amount));
    }
    private XpLedgerEntry Ledger(ulong user, decimal amount) => new()
    {
        GuildId = 1, UserId = user, GrantKey = Guid.NewGuid().ToString("N"), Source = "message", Amount = amount,
        Kind = XpLedgerEntryKind.AutomaticGrant, ProjectionStatus = SeasonProjectionStatus.Applied, OccurredAtUtc = clock.GetUtcNow().UtcDateTime
    };
    private LevelTransitionEvent Event(LevelTransitionStatus status) => new()
    {
        GuildId = 1, UserId = 2, EventKey = "existing", Source = "message", NewLevel = 1, NewTotalXp = 100,
        Status = status, CreatedAtUtc = clock.GetUtcNow().UtcDateTime
    };
    private Task<MemberXp> Member(ulong user) => db.MemberXp.Find(x => x.UserId == user).SingleAsync();
    private Task<List<LevelTransitionEvent>> Events() => db.LevelTransitionEvents.Find(FilterDefinition<LevelTransitionEvent>.Empty).ToListAsync();
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
    private sealed class Errors : IOperationalErrorRecorder
    {
        public long PersistenceFailureCount => 0;
        public readonly List<OperationalErrorWrite> Records = [];
        public Task<bool> RecordAsync(OperationalErrorWrite error, CancellationToken ct = default) { Records.Add(error); return Task.FromResult(true); }
    }
    private sealed class Realtime : ILeaderboardRealtimePublisher
    {
        public Task PublishMemberAsync(ulong guild, ulong user, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishGuildAsync(ulong guild, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishSettingsAsync(GuildLeaderboardSettings settings, string? previousAlias = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
