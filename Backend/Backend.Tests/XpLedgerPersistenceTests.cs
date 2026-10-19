using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Data.Analytics;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Xp;
using Rankoon.Data.Xp.Import;
using Xunit;

namespace Backend.Tests;

/// <summary>Real MongoDB CAS/unique-index tests, isolated from all application data.</summary>
public sealed class XpLedgerPersistenceTests : IAsyncLifetime
{
    private readonly string databaseName = $"rankoon_xp_test_{Guid.NewGuid():N}";
    private MongoClient client = null!;
    private RankoonDbContext db = null!;
    private XpService xp = null!;
    private VoiceActivityAccumulator accumulator = null!;
    private VoiceActivityProjectionService voice = null!;
    private readonly Clock clock = new();
    private Coordinator coordinator = null!;
    private Transitions transitions = null!;
    private readonly Realtime realtime = new();
    private readonly Analytics analytics = new();

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("RANKOON_SEASON_TEST_MONGO");
        if (connection == null) return;
        client = new MongoClient(connection);
        db = new(Options.Create(new MongoDbSettings { ConnectionString = connection, DatabaseName = databaseName }));
        await Unique(db.XpLedger, Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.GrantKey));
        await db.XpLedger.Indexes.CreateOneAsync(new CreateIndexModel<XpLedgerEntry>(Builders<XpLedgerEntry>.IndexKeys.Ascending(x => x.ReversesLedgerEntryId), new() { Unique = true, Sparse = true }));
        await Unique(db.MemberXp, Builders<MemberXp>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UserId));
        await Unique(db.GuildStats, Builders<GuildStats>.IndexKeys.Ascending(x => x.GuildId));
        await Unique(db.SeasonMemberXp, Builders<SeasonMemberXp>.IndexKeys.Ascending(x => x.SeasonId).Ascending(x => x.UserId));
        await Unique(db.LevelTransitionEvents, Builders<LevelTransitionEvent>.IndexKeys.Ascending(x => x.EventKey));
        await Unique(db.VoiceActivities, Builders<VoiceActivityDay>.IndexKeys.Ascending(x => x.GuildId).Ascending(x => x.UserId).Ascending(x => x.DayStartUtc).Ascending(x => x.Part));
        coordinator = new(new XpProjectionCoordinator(db, clock));
        transitions = new(new LevelTransitionService(db, clock));
        xp = new(db, new SeasonService(db, clock), analytics, realtime, transitions, coordinator,
            new GuildXpSettingsRuntimeCache(db), new Changes(), clock, NullLogger<XpService>.Instance);
        accumulator = new(db, Options.Create(new VoiceActivityOptions()), NullLogger<VoiceActivityAccumulator>.Instance);
        voice = new(db, coordinator, transitions, realtime, clock, Options.Create(new VoiceActivityOptions()));
    }

    public Task DisposeAsync() => client == null ? Task.CompletedTask : client.DropDatabaseAsync(databaseName);

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task All_sources_are_idempotent_and_zero_xp_never_creates_a_ledger_entry()
    {
        foreach (var source in new[] { "message", "reaction", "thread_message", "thread_create", "event_interest", "voice" })
        {
            var request = Grant(source, source);
            Assert.True(await xp.GrantAsync(request));
            Assert.False(await xp.GrantAsync(request));
        }
        Assert.False(await xp.GrantAsync(Grant("zero") with { Amount = 0 }));
        Assert.Equal(6, await db.XpLedger.CountDocumentsAsync(FilterDefinition<XpLedgerEntry>.Empty));
        Assert.Equal(60, (await xp.GetMemberAsync(1, 2))!.TotalXp);
        Assert.Equal(60, (await Stats()).XpAwarded);
        Assert.Equal(6, analytics.Records.Count);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Concurrent_redeliveries_of_one_event_only_award_once()
    {
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => xp.GrantAsync(Grant("same"))));
        await Drain();
        Assert.Single(await db.XpLedger.Find(FilterDefinition<XpLedgerEntry>.Empty).ToListAsync());
        Assert.Equal(10, (await xp.GetMemberAsync(1, 2))!.TotalXp);
        Assert.Equal(10, (await Stats()).XpAwarded);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Concurrent_distinct_events_for_multiple_members_converge_without_lost_or_duplicate_xp()
    {
        await Task.WhenAll(Enumerable.Range(0, 40).Select(index => xp.GrantAsync(Grant($"event:{index}") with { UserId = (ulong)(index % 4 + 2) })));
        await Drain();
        var members = await db.MemberXp.Find(FilterDefinition<MemberXp>.Empty).ToListAsync();
        Assert.Equal(4, members.Count);
        Assert.All(members, member => Assert.Equal(100, member.TotalXp));
        Assert.Equal(400, (await Stats()).XpAwarded);
        Assert.Equal(40, (await Stats()).Messages);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Cooldown_denial_is_durable_and_repair_cannot_bypass_it()
    {
        Assert.True(await xp.GrantAsync(Grant("first") with { CooldownSeconds = 60 }));
        var pending = Entry("pending"); pending.CooldownSource = "message"; pending.CooldownSeconds = 60;
        await db.XpLedger.InsertOneAsync(pending);
        await xp.ProjectAsync(pending, default);
        var denied = await Ledger("pending");
        Assert.True(denied.CooldownDenied);
        Assert.Equal(SeasonProjectionStatus.Applied, denied.ProjectionStatus);
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.False(await xp.GrantAsync(Grant("pending") with { CooldownSeconds = 60 }));
        Assert.False(await xp.ReverseGrantAsync("pending", "reverse-denied", "message"));
        Assert.Equal(10, (await xp.GetMemberAsync(1, 2))!.TotalXp);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Interrupted_cooldown_acquisition_recognizes_the_key_without_extending_the_cooldown()
    {
        var originalTime = clock.GetUtcNow().UtcDateTime;
        await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 2, LastMessageXpAt = originalTime, LastMessageXpGrantKey = "interrupted" });
        var entry = Entry("interrupted"); entry.CooldownSource = "message"; entry.CooldownSeconds = 60;
        await db.XpLedger.InsertOneAsync(entry);
        clock.Advance(TimeSpan.FromMinutes(2));
        await xp.ProjectAsync(entry, default);
        var member = (await xp.GetMemberAsync(1, 2))!;
        Assert.Equal(originalTime, member.LastMessageXpAt);
        Assert.Equal(10, member.TotalXp);
        Assert.True((await Ledger("interrupted")).CooldownAcquired);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Parallel_events_share_one_cooldown_and_the_exact_boundary_is_eligible()
    {
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => xp.GrantAsync(Grant($"cool:{i}") with { CooldownSeconds = 60 })));
        await Drain();
        Assert.Equal(10, (await xp.GetMemberAsync(1, 2))!.TotalXp);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(await xp.GrantAsync(Grant("boundary") with { CooldownSeconds = 60 }));
        Assert.Equal(20, (await xp.GetMemberAsync(1, 2))!.TotalXp);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Ledger_crash_after_increment_then_lock_contention_preserves_xp_and_rank_up()
    {
        transitions.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => xp.GrantAsync(Grant("crash") with { Amount = 100 }));
        Assert.Equal(100, (await xp.GetMemberAsync(1, 2))!.TotalXp);
        clock.Advance(TimeSpan.FromMinutes(3));
        coordinator.DenyNext = true;
        await xp.ProjectAsync(await Ledger("crash"), default);
        Assert.NotNull((await Ledger("crash")).ProjectionLeaseOwner);
        await xp.ProjectAsync(await Ledger("crash"), default);
        Assert.Equal(100, (await xp.GetMemberAsync(1, 2))!.TotalXp);
        Assert.Equal(100, (await Stats()).XpAwarded);
        var transition = Assert.Single(await db.LevelTransitionEvents.Find(FilterDefinition<LevelTransitionEvent>.Empty).ToListAsync());
        Assert.Equal(0, transition.PreviousLevel);
        Assert.Equal(1, transition.NewLevel);
        Assert.Equal(SeasonProjectionStatus.Applied, (await Ledger("crash")).ProjectionStatus);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Voice_crash_after_increment_then_lock_contention_preserves_target_and_rank_up()
    {
        await accumulator.AccrueAsync(Slice(0, 600));
        transitions.FailNext = true;
        var firstDay = await Day();
        await Assert.ThrowsAsync<InvalidOperationException>(() => voice.ProjectAsync(firstDay, "Member"));
        Assert.Equal(100, (await xp.GetMemberAsync(1, 2))!.TotalXp);
        clock.Advance(TimeSpan.FromMinutes(3));
        coordinator.DenyNext = true;
        await voice.ProjectAsync(await Day(), "Member");
        Assert.Equal(VoiceActivityProjectionStatus.Projecting, (await Day()).ProjectionStatus);
        await voice.ProjectAsync(await Day(), "Member");
        Assert.Equal(100, (await xp.GetMemberAsync(1, 2))!.TotalXp);
        Assert.Equal(100, (await Stats()).VoiceXpAwarded);
        var transition = Assert.Single(await db.LevelTransitionEvents.Find(FilterDefinition<LevelTransitionEvent>.Empty).ToListAsync());
        Assert.Equal(0, transition.PreviousLevel);
        Assert.Equal(1, transition.NewLevel);
        Assert.False(transition.SuppressAnnouncement);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Checkpoint_arriving_during_projection_keeps_new_activity_pending_and_projects_only_the_remainder()
    {
        await accumulator.AccrueAsync(Slice(0, 60));
        transitions.BeforeEnsure = async () => await accumulator.AccrueAsync(Slice(60, 120));
        await voice.ProjectAsync(await Day(), "Member");
        var pending = await Day();
        Assert.Equal(VoiceActivityProjectionStatus.Pending, pending.ProjectionStatus);
        Assert.Equal(20, pending.TotalAwardedXp);
        Assert.Equal(10, pending.ProjectedXp);
        await voice.ProjectAsync(pending, "Member");
        Assert.Equal(20, (await xp.GetMemberAsync(1, 2))!.TotalXp);
        Assert.Equal(120, (await xp.GetMemberAsync(1, 2))!.VoiceSeconds);
        Assert.Equal(VoiceActivityProjectionStatus.Applied, (await Day()).ProjectionStatus);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Stale_checkpoint_cannot_overwrite_a_completed_projection()
    {
        await accumulator.AccrueAsync(Slice(0, 60));
        var stale = await Day();
        // Plan from the exact revision an accumulator could have read before completion.
        transitions.BeforeEnsure = async () => stale = await Day();
        await voice.ProjectAsync(await Day(), "Member");
        var replacement = VoiceActivityAccumulator.Plan(stale, Slice(60, 120)).Replacement!;
        var write = await db.VoiceActivities.ReplaceOneAsync(x => x.Id == stale.Id && x.Revision == stale.Revision, replacement);
        Assert.Equal(0, write.ModifiedCount);
        await accumulator.AccrueAsync(Slice(60, 120));
        await voice.ProjectAsync(await Day(), "Member");
        Assert.Equal(20, (await xp.GetMemberAsync(1, 2))!.TotalXp);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Automatic_reversals_are_unique_per_original_even_with_different_retry_keys()
    {
        await xp.GrantAsync(Grant("reaction", "reaction"));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => xp.ReverseGrantAsync("reaction", $"reverse:{i}", "reaction")));
        await Drain();
        Assert.Equal(0, (await xp.GetMemberAsync(1, 2))!.TotalXp);
        Assert.Equal(0, (await Stats()).XpAwarded);
        Assert.Equal(2, await db.XpLedger.CountDocumentsAsync(FilterDefinition<XpLedgerEntry>.Empty));
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task An_existing_grant_key_cannot_be_reused_for_another_member()
    {
        await xp.GrantAsync(Grant("identity"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => xp.GrantAsync(Grant("identity") with { UserId = 3 }));
        Assert.Null(await xp.GetMemberAsync(1, 3));
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Recovery_preserves_imported_xp_manual_scope_and_compressed_voice()
    {
        await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 2, ImportedMee6Xp = 50, TotalXp = 50 });
        await accumulator.AccrueAsync(Slice(0, 60));
        await voice.ProjectAsync(await Day(), "Member");
        var adjustment = Entry("manual"); adjustment.Kind = XpLedgerEntryKind.ManualAdjustment; adjustment.Amount = -5; adjustment.Scope = XpLedgerScope.LifetimeOnly;
        await db.XpLedger.InsertOneAsync(adjustment);
        await xp.ProjectAsync(adjustment, default);
        transitions.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => xp.GrantAsync(Grant("recover")));
        clock.Advance(TimeSpan.FromMinutes(3));
        await xp.ProjectAsync(await Ledger("recover"), default);
        var member = (await xp.GetMemberAsync(1, 2))!;
        Assert.Equal(50, member.ImportedMee6Xp);
        Assert.Equal(-5, member.ManualAdjustment);
        Assert.Equal(20, member.EarnedXp);
        Assert.Equal(10, member.VoiceXp);
        Assert.Equal(65, member.TotalXp);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Ledger_recovery_after_voice_cutover_preserves_uncopied_awards_and_server_totals()
    {
        await SeedVoiceCutoverAsync();
        transitions.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => xp.GrantAsync(Grant("recover-cutover")));
        clock.Advance(TimeSpan.FromMinutes(3));
        await xp.ProjectAsync(await Ledger("recover-cutover"), default);
        await xp.ProjectAsync(await Ledger("recover-cutover"), default);
        var member = (await xp.GetMemberAsync(1, 2))!;
        Assert.Equal(50, member.ImportedMee6Xp);
        Assert.Equal(135.125m, member.EarnedXp);
        Assert.Equal(125.125m, member.VoiceXp);
        Assert.Equal(185.125m, member.TotalXp);
        Assert.Equal(135.125m, (await Stats()).XpAwarded);
        Assert.Equal(125.125m, (await Stats()).VoiceXpAwarded);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Voice_recovery_after_cutover_preserves_uncopied_awards_without_readding_copied_grants()
    {
        await SeedVoiceCutoverAsync();
        await accumulator.AccrueAsync(Slice(600, 660) with { SessionId = "live" });
        transitions.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await voice.ProjectAsync(await Day(), "Member"));
        clock.Advance(TimeSpan.FromMinutes(3));
        await voice.ProjectAsync(await Day(), "Member");
        await voice.ProjectAsync(await Day(), "Member");
        var member = (await xp.GetMemberAsync(1, 2))!;
        Assert.Equal(135.125m, member.EarnedXp);
        Assert.Equal(135.125m, member.VoiceXp);
        Assert.Equal(185.125m, member.TotalXp);
        Assert.Equal(135.125m, (await Stats()).XpAwarded);
        Assert.Equal(135.125m, (await Stats()).VoiceXpAwarded);
    }

    private async Task SeedVoiceCutoverAsync()
    {
        var copied = Entry("copied"); copied.Source = "voice"; copied.Amount = 100; copied.ProjectionStatus = SeasonProjectionStatus.Applied;
        var skipped = Entry("skipped"); skipped.Source = "voice"; skipped.Amount = 25.125m; skipped.ProjectionStatus = SeasonProjectionStatus.Applied;
        await db.XpLedger.InsertManyAsync([copied, skipped]);
        // The production legacy shape has no projection_status at all.
        await db.XpLedger.UpdateOneAsync(x => x.Id == skipped.Id, Builders<XpLedgerEntry>.Update
            .Unset(x => x.ProjectionStatus).Unset(x => x.IsProjectionControl).Unset(x => x.CooldownDenied));
        await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 2, ImportedMee6Xp = 50, EarnedXp = 125.125m, VoiceXp = 125.125m, TotalXp = 175.125m, VoiceSeconds = 600 });
        await db.GuildStats.InsertOneAsync(new() { GuildId = 1, XpAwarded = 125.125m, VoiceXpAwarded = 125.125m, VoiceSeconds = 600 });
        await accumulator.AccrueAsync(Slice(0, 600) with
        {
            SessionId = VoiceLedgerMigrationService.LegacySessionId(copied.Id!),
            SettingsRevision = VoiceLedgerMigrationService.LegacySettingsRevision, AlreadyProjected = true
        });
        await db.VoiceLedgerMigrationStates.InsertOneAsync(new()
        {
            SourceHighWatermarkId = skipped.Id, Phase = VoiceLedgerMigrationPhase.AwaitingDeletionApproval,
            Parity = new() { Matches = true, GroupedParityVerified = true, Fingerprint = "verified" }
        });
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task New_cooldown_reservation_preserves_proof_of_an_interrupted_earlier_grant()
    {
        var first = Entry("first"); first.CooldownSource = "message"; first.CooldownSeconds = 60;
        await db.XpLedger.InsertOneAsync(first);
        await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 2, LastMessageXpAt = first.OccurredAtUtc, LastMessageXpGrantKey = first.GrantKey });
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await xp.GrantAsync(Grant("second") with { CooldownSeconds = 60 }));
        Assert.True((await Ledger("first")).CooldownAcquired);
        await xp.ProjectAsync(await Ledger("first"), default);
        Assert.False((await Ledger("first")).CooldownDenied);
        Assert.Equal(20, (await xp.GetMemberAsync(1, 2))!.TotalXp);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Delayed_repairs_use_event_time_instead_of_consuming_the_current_cooldown()
    {
        var first = Entry("first"); first.CooldownSource = "message"; first.CooldownSeconds = 60;
        await db.XpLedger.InsertOneAsync(first);
        var second = Entry("second"); second.CooldownSource = "message"; second.CooldownSeconds = 60; second.OccurredAtUtc = first.OccurredAtUtc.AddSeconds(60);
        await db.XpLedger.InsertOneAsync(second);
        clock.Advance(TimeSpan.FromDays(1));
        await xp.ProjectAsync(first, default);
        await xp.ProjectAsync(second, default);
        Assert.Equal(20, (await xp.GetMemberAsync(1, 2))!.TotalXp);
        Assert.Equal(second.OccurredAtUtc, (await xp.GetMemberAsync(1, 2))!.LastMessageXpAt);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Removing_a_pending_cooldown_denied_reaction_never_subtracts_unearned_xp()
    {
        await xp.GrantAsync(Grant("awarded", "reaction") with { CooldownSeconds = 60 });
        var waiting = Entry("waiting"); waiting.Source = "reaction"; waiting.CooldownSource = "reaction"; waiting.CooldownSeconds = 60;
        await db.XpLedger.InsertOneAsync(waiting);
        await xp.ReverseGrantAsync("waiting", "removed", "reaction");
        Assert.Equal(10, (await xp.GetMemberAsync(1, 2))!.TotalXp);
        Assert.Equal(SeasonProjectionStatus.Pending, (await Ledger("removed")).ProjectionStatus);
        await Drain();
        Assert.Equal(10, (await xp.GetMemberAsync(1, 2))!.TotalXp);
        Assert.True((await Ledger("removed")).CooldownDenied);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Removing_a_pending_eligible_reaction_eventually_reverses_it_exactly_once()
    {
        var waiting = Entry("waiting"); waiting.Source = "reaction";
        await db.XpLedger.InsertOneAsync(waiting);
        await xp.ReverseGrantAsync("waiting", "removed", "reaction");
        Assert.Null(await xp.GetMemberAsync(1, 2));
        await Drain();
        Assert.Equal(0, (await xp.GetMemberAsync(1, 2))!.TotalXp);
        Assert.Equal(0, (await Stats()).XpAwarded);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Legacy_voice_grants_and_reversals_keep_all_voice_counters_consistent()
    {
        await xp.GrantAsync(Grant("voice", "voice") with { PeriodStartsAtUtc = DateTime.UnixEpoch, PeriodEndsAtUtc = DateTime.UnixEpoch.AddMinutes(1) });
        var member = (await xp.GetMemberAsync(1, 2))!;
        Assert.Equal(10, member.VoiceXp); Assert.Equal(60, member.VoiceSeconds);
        Assert.Equal(10, (await Stats()).VoiceXpAwarded); Assert.Equal(60, (await Stats()).VoiceSeconds);
        await xp.ReverseGrantAsync("voice", "voice-reversal", "voice");
        member = (await xp.GetMemberAsync(1, 2))!;
        Assert.Equal(0, member.TotalXp); Assert.Equal(0, member.VoiceXp); Assert.Equal(0, member.VoiceSeconds);
        Assert.Equal(0, (await Stats()).VoiceXpAwarded); Assert.Equal(0, (await Stats()).VoiceSeconds);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Ledger_and_voice_rank_ups_survive_failure_in_both_lifetime_and_season_projections()
    {
        var season = new GuildSeason { GuildId = 1, Name = "Season", Status = SeasonStatus.Active, StartsAtUtc = clock.GetUtcNow().UtcDateTime.AddDays(-1), EndsAtUtc = clock.GetUtcNow().UtcDateTime.AddDays(1) };
        await db.GuildSeasons.InsertOneAsync(season);
        await db.GuildSeasonSettings.InsertOneAsync(new() { GuildId = 1, Enabled = true });
        transitions.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => xp.GrantAsync(Grant("season-grant") with { Amount = 100 }));
        clock.Advance(TimeSpan.FromMinutes(3));
        await xp.ProjectAsync(await Ledger("season-grant"), default);
        Assert.Equal(100, (await db.SeasonMemberXp.Find(x => x.SeasonId == season.Id).SingleAsync()).TotalXp);
        var events = await db.LevelTransitionEvents.Find(FilterDefinition<LevelTransitionEvent>.Empty).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => { Assert.Equal(0, e.PreviousLevel); Assert.Equal(1, e.NewLevel); });
        await accumulator.AccrueAsync(Slice(0, 600) with { UserId = 3, SeasonId = season.Id });
        transitions.FailNext = true;
        var day = await Day();
        await Assert.ThrowsAsync<InvalidOperationException>(() => voice.ProjectAsync(day, "Other"));
        clock.Advance(TimeSpan.FromMinutes(3));
        await voice.ProjectAsync(await Day(), "Other");
        Assert.Equal(100, (await db.SeasonMemberXp.Find(x => x.SeasonId == season.Id && x.UserId == 3).SingleAsync()).TotalXp);
        events = await db.LevelTransitionEvents.Find(x => x.UserId == 3).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => { Assert.Equal(0, e.PreviousLevel); Assert.Equal(1, e.NewLevel); Assert.False(e.SuppressAnnouncement); });
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Recovered_manual_adjustments_keep_lifetime_and_season_scopes_separate()
    {
        var season = new GuildSeason { GuildId = 1, Name = "Season", Status = SeasonStatus.Active };
        await db.GuildSeasons.InsertOneAsync(season);
        foreach (var scope in Enum.GetValues<XpLedgerScope>())
        {
            var userId = (ulong)(10 + (int)scope);
            await db.MemberXp.InsertOneAsync(new() { GuildId = 1, UserId = userId, ImportedMee6Xp = 100, TotalXp = 100 });
            await db.SeasonMemberXp.InsertOneAsync(new() { GuildId = 1, UserId = userId, SeasonId = season.Id!, StartingXp = 50, TotalXp = 50 });
            var entry = Entry($"manual:{scope}"); entry.UserId = userId; entry.Amount = 20; entry.Kind = XpLedgerEntryKind.ManualAdjustment; entry.Scope = scope; entry.SeasonId = season.Id;
            await db.XpLedger.InsertOneAsync(entry);
            transitions.FailNext = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => xp.ProjectAsync(entry, default));
            clock.Advance(TimeSpan.FromMinutes(3));
            await xp.ProjectAsync(await Ledger(entry.GrantKey), default);
            Assert.Equal(scope == XpLedgerScope.SeasonOnly ? 100 : 120, (await xp.GetMemberAsync(1, userId))!.TotalXp);
            Assert.Equal(scope == XpLedgerScope.LifetimeOnly ? 50 : 70, (await db.SeasonMemberXp.Find(x => x.SeasonId == season.Id && x.UserId == userId).SingleAsync()).TotalXp);
        }
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Closed_seasons_remain_frozen_while_lifetime_continues()
    {
        var season = new GuildSeason { GuildId = 1, Name = "Closed", Status = SeasonStatus.Closed };
        await db.GuildSeasons.InsertOneAsync(season);
        await db.SeasonMemberXp.InsertOneAsync(new() { GuildId = 1, UserId = 2, SeasonId = season.Id!, StartingXp = 50, TotalXp = 50 });
        var entry = Entry("late"); entry.Amount = 100; entry.SeasonId = season.Id;
        await db.XpLedger.InsertOneAsync(entry);
        await xp.ProjectAsync(entry, default);
        await accumulator.AccrueAsync(Slice(0, 600) with { SeasonId = season.Id });
        await voice.ProjectAsync(await Day(), "Member");
        Assert.Equal(200, (await xp.GetMemberAsync(1, 2))!.TotalXp);
        Assert.Equal(50, (await db.SeasonMemberXp.Find(x => x.SeasonId == season.Id).SingleAsync()).TotalXp);
        Assert.Empty(await db.LevelTransitionEvents.Find(x => x.Scope == LevelProgressScope.Season).ToListAsync());
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Partial_lock_acquisition_releases_the_guild_and_expired_owners_cannot_renew()
    {
        var inner = new XpProjectionCoordinator(db, clock);
        await db.XpProjectionLeases.InsertOneAsync(new() { Key = "member:1:2", GuildId = 1, UserId = 2, OwnerId = "busy", ExpiresAtUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(1) });
        Assert.Null(await inner.AcquireAsync(1, 2, null));
        Assert.False(await db.XpProjectionLeases.Find(x => x.Key == "guild:1").AnyAsync());
        await using var other = await inner.AcquireAsync(1, 3, null);
        Assert.NotNull(other);
        Assert.True(await other.IsOwnedAsync());
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.False(await other.RenewAsync());
        await using var replacement = await inner.AcquireAsync(1, 3, null);
        Assert.NotNull(replacement);
        await other.DisposeAsync();
        Assert.True(await replacement.IsOwnedAsync());
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Repeated_imports_preserve_live_earned_xp_and_wait_for_projection_ownership()
    {
        var import = new XpImportService(null!, db, coordinator, null!, null!, realtime, null!, clock, NullLogger<XpImportService>.Instance);
        await xp.GrantAsync(Grant("earned"));
        var writes = new[] { new XpImportMember(2, "Imported member", 100, 5, null) };
        var owner = (await coordinator.AcquireAsync(1, 2, null))!;
        var pendingImport = import.WriteMembersAsync(1, writes, new Dictionary<ulong, bool>(), clock.GetUtcNow().UtcDateTime, default);
        Assert.False(pendingImport.IsCompleted);
        Assert.Equal(0, (await xp.GetMemberAsync(1, 2))!.ImportedMee6Xp);
        await owner.DisposeAsync();
        await pendingImport;
        await import.WriteMembersAsync(1, writes, new Dictionary<ulong, bool>(), clock.GetUtcNow().UtcDateTime, default);
        Assert.Equal(110, (await xp.GetMemberAsync(1, 2))!.TotalXp);
        Assert.Equal(10, (await xp.GetMemberAsync(1, 2))!.EarnedXp);
        await xp.GrantAsync(Grant("after-import"));
        Assert.Equal(120, (await xp.GetMemberAsync(1, 2))!.TotalXp);
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Cancelled_overlapping_seasons_cannot_break_voice_accrual_and_disabled_seasons_get_no_voice_attribution()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var status in Enum.GetValues<SeasonStatus>())
            await db.GuildSeasons.InsertOneAsync(new() { GuildId = 1, Status = status, StartsAtUtc = now.AddDays(-1), EndsAtUtc = now.AddDays(1) });
        var watchdog = new VoiceXpWatchdog(null!, db, null!, null!, null!, null!, null!, null!, null!, null!, clock,
            Options.Create(new VoiceWatchdogOptions()), Options.Create(new VoiceActivityOptions()), NullLogger<VoiceXpWatchdog>.Instance);
        Assert.Empty(await watchdog.LoadSeasonsAsync(1, now.AddMinutes(-1), now));
        await db.GuildSeasonSettings.InsertOneAsync(new() { GuildId = 1, Enabled = true });
        var seasons = await watchdog.LoadSeasonsAsync(1, now.AddMinutes(-1), now);
        Assert.Equal(2, seasons.Count);
        Assert.All(seasons, season => Assert.True(season.Status is SeasonStatus.Active or SeasonStatus.Closing));
        await db.GuildSeasons.DeleteManyAsync(x => x.Status == SeasonStatus.Closing);
        // The cancelled season still overlaps the active season. It must never be
        // included in SingleOrDefault attribution for a checkpoint.
        seasons = await watchdog.LoadSeasonsAsync(1, now.AddMinutes(-1), now);
        Assert.Equal(SeasonStatus.Active, seasons.Single(x => x.StartsAtUtc <= now && now < x.EndsAtUtc).Status);
        await db.GuildSeasonSettings.UpdateOneAsync(x => x.GuildId == 1, Builders<GuildSeasonSettings>.Update.Set(x => x.Enabled, false));
        Assert.Empty(await watchdog.LoadSeasonsAsync(1, now.AddMinutes(-1), now));
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Xp_history_never_displays_cooldown_denials_as_awarded_xp()
    {
        await xp.GrantAsync(Grant("accepted") with { CooldownSeconds = 60 });
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await xp.GrantAsync(Grant("denied") with { CooldownSeconds = 60 }));
        var audit = new XpAuditService(db, xp, new SeasonService(db, clock), null!, clock, new ConfigurationBuilder().Build(), null!);
        var filter = new XpAuditEntryFilter(null, null, null, null, null, null, null, null, null);
        var entries = await audit.GetEntriesAsync(1, 2, filter);
        Assert.Equal("accepted", Assert.Single(entries.Items).GrantKey);
        var timeline = await audit.GetTimelineAsync(1, 2, filter);
        Assert.Equal(10, Assert.Single(timeline.Items).TotalAmount);
        Assert.True((await Ledger("denied")).CooldownDenied);
        Assert.Equal(2, await db.XpLedger.CountDocumentsAsync(FilterDefinition<XpLedgerEntry>.Empty));
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Ledger_recovers_every_partial_projection_prefix_after_an_abrupt_process_exit()
    {
        for (var writesCompleted = 0; writesCompleted <= 3; writesCompleted++)
        {
            var guild = (ulong)(10 + writesCompleted);
            var season = new GuildSeason { GuildId = guild, Status = SeasonStatus.Active, Name = "Season" };
            await db.GuildSeasons.InsertOneAsync(season);
            var entry = Entry($"partial:{writesCompleted}"); entry.GuildId = guild; entry.Amount = 100; entry.SeasonId = season.Id;
            entry.ProjectionLeaseOwner = "dead-process"; entry.ProjectionLeaseExpiresAtUtc = clock.GetUtcNow().UtcDateTime.AddSeconds(-1);
            entry.LevelTransitionSnapshot = XpService.TransitionSnapshot(0, 100);
            entry.SeasonLevelTransitionSnapshots = new() { [season.Id!] = XpService.TransitionSnapshot(0, 100) };
            await db.XpLedger.InsertOneAsync(entry);
            if (writesCompleted >= 1) await db.MemberXp.InsertOneAsync(new() { GuildId = guild, UserId = 2, EarnedXp = 100, TotalXp = 100, MessageCount = 1 });
            if (writesCompleted >= 2) await db.SeasonMemberXp.InsertOneAsync(new() { GuildId = guild, SeasonId = season.Id!, UserId = 2, EarnedXp = 100, TotalXp = 100, MessageCount = 1 });
            if (writesCompleted >= 3) await db.GuildStats.InsertOneAsync(new() { GuildId = guild, XpAwarded = 100, Messages = 1 });
            await xp.ProjectAsync(entry, default);
            var member = (await xp.GetMemberAsync(guild, 2))!;
            Assert.Equal(100, member.TotalXp); Assert.Equal(1, member.MessageCount);
            var seasonMember = await db.SeasonMemberXp.Find(x => x.SeasonId == season.Id && x.UserId == 2).SingleAsync();
            Assert.Equal(100, seasonMember.TotalXp); Assert.Equal(1, seasonMember.MessageCount);
            var stats = await db.GuildStats.Find(x => x.GuildId == guild).SingleAsync();
            Assert.Equal(100, stats.XpAwarded); Assert.Equal(1, stats.Messages);
            Assert.Equal(2, await db.LevelTransitionEvents.CountDocumentsAsync(x => x.GuildId == guild));
            Assert.Equal(SeasonProjectionStatus.Applied, (await Ledger(entry.GrantKey)).ProjectionStatus);
        }
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Voice_recovers_every_partial_projection_prefix_and_repeated_checkpoints_stay_idempotent()
    {
        for (var writesCompleted = 0; writesCompleted <= 3; writesCompleted++)
        {
            var guild = (ulong)(10 + writesCompleted);
            var season = new GuildSeason { GuildId = guild, Status = SeasonStatus.Active, Name = "Season" };
            await db.GuildSeasons.InsertOneAsync(season);
            await accumulator.AccrueAsync(Slice(0, 600) with { GuildId = guild, SeasonId = season.Id });
            var day = await db.VoiceActivities.Find(x => x.GuildId == guild).SingleAsync();
            day.ProjectionStatus = VoiceActivityProjectionStatus.Projecting;
            day.ProjectionLeaseOwner = "dead-process"; day.ProjectionLeaseExpiresAtUtc = clock.GetUtcNow().UtcDateTime.AddSeconds(-1);
            day.ProjectionTargetRevision = day.ProjectionRevision;
            day.ProjectionTargetAwardedXp = day.TotalAwardedXp; day.ProjectionTargetEligibleSeconds = day.TotalEligibleSeconds;
            day.ProjectionTargetSeasonTotals = day.SeasonTotals;
            day.LevelTransitionSnapshot = XpService.TransitionSnapshot(0, 100);
            day.SeasonLevelTransitionSnapshots = new() { [season.Id!] = XpService.TransitionSnapshot(0, 100) };
            await db.VoiceActivities.ReplaceOneAsync(x => x.Id == day.Id, day);
            if (writesCompleted >= 1) await db.MemberXp.InsertOneAsync(new() { GuildId = guild, UserId = 2, VoiceXp = 100, EarnedXp = 100, TotalXp = 100, VoiceSeconds = 600 });
            if (writesCompleted >= 2) await db.SeasonMemberXp.InsertOneAsync(new() { GuildId = guild, SeasonId = season.Id!, UserId = 2, VoiceXp = 100, EarnedXp = 100, TotalXp = 100, VoiceSeconds = 600 });
            if (writesCompleted >= 3) await db.GuildStats.InsertOneAsync(new() { GuildId = guild, XpAwarded = 100, VoiceXpAwarded = 100, VoiceSeconds = 600 });
            await voice.ProjectAsync(day, "Member");
            await voice.ProjectAsync(await db.VoiceActivities.Find(x => x.Id == day.Id).SingleAsync(), "Member");
            var member = (await xp.GetMemberAsync(guild, 2))!;
            Assert.Equal(100, member.TotalXp); Assert.Equal(100, member.VoiceXp); Assert.Equal(600, member.VoiceSeconds);
            var seasonMember = await db.SeasonMemberXp.Find(x => x.SeasonId == season.Id && x.UserId == 2).SingleAsync();
            Assert.Equal(100, seasonMember.TotalXp); Assert.Equal(600, seasonMember.VoiceSeconds);
            var stats = await db.GuildStats.Find(x => x.GuildId == guild).SingleAsync();
            Assert.Equal(100, stats.XpAwarded); Assert.Equal(100, stats.VoiceXpAwarded); Assert.Equal(600, stats.VoiceSeconds);
            Assert.Equal(2, await db.LevelTransitionEvents.CountDocumentsAsync(x => x.GuildId == guild));
        }
    }

    private XpGrantRequest Grant(string key, string source = "message") => new(1, 2, "Member", source, 10, key, clock.GetUtcNow().UtcDateTime);
    private XpLedgerEntry Entry(string key) => new() { GuildId = 1, UserId = 2, DisplayName = "Member", Source = "message", GrantKey = key, Amount = 10, OccurredAtUtc = clock.GetUtcNow().UtcDateTime, CreatedAt = clock.GetUtcNow().UtcDateTime };
    private VoiceAccrualSlice Slice(int from, int to) => new(1, 2, "session", DateTime.UnixEpoch.AddSeconds(from), DateTime.UnixEpoch.AddSeconds(to), 3, null, to - from, (to - from) / 6m, 10, 1, null, 1, clock.GetUtcNow().UtcDateTime);
    private Task<XpLedgerEntry> Ledger(string key) => db.XpLedger.Find(x => x.GrantKey == key).SingleAsync();
    private Task<VoiceActivityDay> Day() => db.VoiceActivities.Find(FilterDefinition<VoiceActivityDay>.Empty).SingleAsync();
    private Task<GuildStats> Stats() => db.GuildStats.Find(x => x.GuildId == 1).SingleAsync();
    private static Task<string> Unique<T>(IMongoCollection<T> collection, IndexKeysDefinition<T> keys) => collection.Indexes.CreateOneAsync(new CreateIndexModel<T>(keys, new() { Unique = true }));
    private async Task Drain()
    {
        for (var i = 0; i < 10; i++)
        {
            var pending = await db.XpLedger.Find(x => x.ProjectionStatus == SeasonProjectionStatus.Pending).ToListAsync();
            if (pending.Count == 0) return;
            foreach (var entry in pending) await xp.ProjectAsync(entry, default);
        }
        Assert.Fail("XP ledger did not drain after ten repair passes.");
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
    private sealed class Coordinator(IXpProjectionCoordinator inner) : IXpProjectionCoordinator
    {
        public bool DenyNext;
        public Task<IXpProjectionLease?> AcquireAsync(ulong guild, ulong user, string? season, CancellationToken ct = default)
        {
            if (DenyNext) { DenyNext = false; return Task.FromResult<IXpProjectionLease?>(null); }
            return inner.AcquireAsync(guild, user, season, ct);
        }
    }
    private sealed class Transitions(ILevelTransitionService inner) : ILevelTransitionService
    {
        public bool FailNext;
        public Func<Task>? BeforeEnsure;
        private async Task Before()
        {
            if (FailNext) { FailNext = false; throw new InvalidOperationException("Injected crash after XP projection."); }
            var callback = BeforeEnsure; BeforeEnsure = null;
            if (callback != null) await callback();
        }
        public async Task EnsureAsync(XpLedgerEntry ledger, LevelTransitionSnapshot snapshot, CancellationToken ct = default) { await Before(); await inner.EnsureAsync(ledger, snapshot, ct); }
        public async Task EnsureAsync(ulong guild, ulong user, LevelTransitionCause cause, LevelTransitionSnapshot snapshot, CancellationToken ct = default) { await Before(); await inner.EnsureAsync(guild, user, cause, snapshot, ct); }
        public async Task EnsureAsync(ulong guild, ulong user, LevelTransitionCause cause, LevelTransitionSnapshot snapshot, LevelProgressScope scope, string? season, string? name, CancellationToken ct = default) { await Before(); await inner.EnsureAsync(guild, user, cause, snapshot, scope, season, name, ct); }
    }
    private sealed class Changes : IGuildXpSettingsChangePublisher
    {
        public ValueTask PublishAsync(GuildXpSettingsChanged change, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
    private sealed class Analytics : IGuildAnalyticsRecorder
    {
        public long DroppedCount => 0;
        public readonly System.Collections.Concurrent.ConcurrentBag<GuildAnalyticsWrite> Records = [];
        public bool TryRecord(GuildAnalyticsWrite value) { Records.Add(value); return true; }
    }
    private sealed class Realtime : ILeaderboardRealtimePublisher
    {
        public Task PublishMemberAsync(ulong guild, ulong user, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishGuildAsync(ulong guild, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishSettingsAsync(GuildLeaderboardSettings settings, string? previousAlias = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
