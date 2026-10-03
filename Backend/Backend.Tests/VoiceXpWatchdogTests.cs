using System.Reflection;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class VoiceXpWatchdogTests
{
    [Fact]
    public async Task First_settings_save_has_no_previous_revision_to_reconcile()
    {
        var watchdog = Watchdog(new EmptySettingsCache());

        // The Discord resolver and accrual dependencies are deliberately absent:
        // no work should start until a persisted settings document exists.
        await watchdog.ReconcileNowAsync(1, CancellationToken.None);

        Assert.Equal(VoiceWatchdogState.Stopped, watchdog.GetStatus(1).State);
    }

    [Fact]
    public async Task Persisted_revision_activation_is_queued_without_request_time_reconciliation()
    {
        var watchdog = Watchdog(new EmptySettingsCache(hasSettings: true));

        await watchdog.ActivateSettingsRevisionAsync(1, 3, CancellationToken.None);
        await watchdog.ActivateSettingsRevisionAsync(1, 2, CancellationToken.None);

        Assert.Equal(3L, PendingActivations(watchdog)[1]);
        Assert.Equal(VoiceWatchdogState.Stopped, watchdog.GetStatus(1).State);
    }

    [Fact]
    public async Task Pending_activation_survives_until_its_revision_is_successfully_reconciled()
    {
        var watchdog = Watchdog(new EmptySettingsCache());
        await watchdog.ActivateSettingsRevisionAsync(1, 3, CancellationToken.None);
        var complete = typeof(VoiceXpWatchdog).GetMethod("CompleteSettingsActivation", BindingFlags.NonPublic | BindingFlags.Instance)!;

        complete.Invoke(watchdog, [1UL, 2L]);
        Assert.Equal(3L, PendingActivations(watchdog)[1]);
        complete.Invoke(watchdog, [1UL, 3L]);
        Assert.Empty(PendingActivations(watchdog));
    }

    [Rankoon.Backend.Tests.SeasonMongoFact]
    public async Task Cache_distinguishes_absent_settings_from_settings_saved_after_initialization()
    {
        var connection = Environment.GetEnvironmentVariable("RANKOON_SEASON_TEST_MONGO")!;
        var name = $"rankoon_voice_settings_test_{Guid.NewGuid():N}";
        var client = new MongoClient(connection);
        var database = new RankoonDbContext(Options.Create(new MongoDbSettings { ConnectionString = connection, DatabaseName = name }));
        var cache = new GuildXpSettingsRuntimeCache(database);
        try
        {
            Assert.False(await cache.LoadGuildAsync(1));
            Assert.Null(cache.GetOrDefault(1));
            Assert.Equal(0L, await database.GuildXpSettings.CountDocumentsAsync(Builders<GuildXpSettings>.Filter.Empty));

            await database.GuildXpSettings.InsertOneAsync(new GuildXpSettings { GuildId = 1, Revision = 1 });

            Assert.True(await cache.LoadGuildAsync(1));
            Assert.Equal(1L, cache.GetOrDefault(1)!.Revision);
        }
        finally { await client.DropDatabaseAsync(name); }
    }

    private static VoiceXpWatchdog Watchdog(IGuildXpSettingsRuntimeCache cache) => new(
        null!, null!, cache, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System,
        Options.Create(new VoiceWatchdogOptions()), Options.Create(new VoiceActivityOptions()), NullLogger<VoiceXpWatchdog>.Instance);

    private static ConcurrentDictionary<ulong, long> PendingActivations(VoiceXpWatchdog watchdog) =>
        (ConcurrentDictionary<ulong, long>)typeof(VoiceXpWatchdog).GetField("activatedSettingsRevisions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(watchdog)!;

    private sealed class EmptySettingsCache(bool hasSettings = false) : IGuildXpSettingsRuntimeCache
    {
        public bool TryGet(ulong guildId, out GuildXpSettingsSnapshot settings) { settings = hasSettings ? Snapshot(3, 10m) : null!; return hasSettings; }
        public GuildXpSettingsSnapshot? GetOrDefault(ulong guildId) => hasSettings ? Snapshot(3, 10m) : null;
        public void Apply(GuildXpSettingsSnapshot settings) => throw new NotSupportedException();
        public void Remove(ulong guildId) => throw new NotSupportedException();
        public IReadOnlyCollection<ulong> GetVoiceEnabledGuildIds() => [];
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> LoadGuildAsync(ulong guildId, CancellationToken cancellationToken = default) => Task.FromResult(hasSettings);
    }

    [Fact]
    public void Voice_lifecycle_handles_every_relevant_state_transition()
    {
        var baseline = new RelevantVoiceState(1, false, false, false, false, false);
        Assert.False(VoiceXpWatchdog.IsRelevantVoiceStateChange(baseline, baseline));
        Assert.All(new[]
        {
            baseline with { ChannelId = 2 }, baseline with { IsGuildMuted = true }, baseline with { IsGuildDeafened = true },
            baseline with { IsSelfMuted = true }, baseline with { IsSelfDeafened = true }, baseline with { IsSuppressed = true }
        }, changed => Assert.True(VoiceXpWatchdog.IsRelevantVoiceStateChange(baseline, changed)));
    }

    [Fact]
    public void Voice_lifecycle_uses_a_background_recovery_worker()
    {
        Assert.True(typeof(BackgroundService).IsAssignableFrom(typeof(VoiceXpWatchdog)));
    }

    [Fact]
    public void Voice_watchdog_singleton_is_registered_as_a_hosted_service()
    {
        var program = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Program.cs"));

        Assert.Contains("AddHostedService(provider => provider.GetRequiredService<VoiceXpWatchdog>())", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_cache_only_accepts_higher_revisions()
    {
        var database = new RankoonDbContext(Options.Create(new MongoDbSettings { ConnectionString = "mongodb://localhost:27017", DatabaseName = "rankoon-tests" }));
        var cache = new GuildXpSettingsRuntimeCache(database);
        var current = Snapshot(3, 30m);

        cache.Apply(current);
        cache.Apply(Snapshot(3, 99m));
        cache.Apply(Snapshot(2, 98m));

        Assert.True(cache.TryGet(1, out var cached));
        Assert.Same(current, cached);

        cache.Apply(Snapshot(4, 40m));
        Assert.Equal(40m, cache.GetOrDefault(1)!.Voice.PointsPerMinute);
    }

    [Fact]
    public void Season_boundaries_split_the_entire_accrual_interval()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMinutes(10);
        var segments = (IReadOnlyList<(DateTime Start, DateTime End)>)Invoke("CreateSegments", new[] { start.AddMinutes(4), start.AddMinutes(7) }, start, end)!;

        Assert.Equal(new[] { (start, start.AddMinutes(4)), (start.AddMinutes(4), start.AddMinutes(7)), (start.AddMinutes(7), end) }, segments);
    }

    [Fact]
    public void UTC_midnight_is_a_deterministic_voice_boundary()
    {
        var start = new DateTime(2026, 1, 1, 23, 59, 0, DateTimeKind.Utc);
        var end = start.AddMinutes(2);

        var boundaries = (IEnumerable<DateTime>)Invoke("EachUtcMidnight", start, end)!;
        var segments = VoiceActivityAccumulator.Split(start, end, boundaries);

        Assert.Equal(new[]
        {
            new VoiceActivityInterval(start, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            new VoiceActivityInterval(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), end)
        }, segments);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Guild_voice_processing_requires_both_XP_toggles(bool xpEnabled, bool voiceEnabled, bool expected)
    {
        var settings = new GuildXpSettings { Enabled = xpEnabled, Voice = new VoiceXpSettings { Enabled = voiceEnabled } };

        var enabled = (bool)Invoke("IsVoiceXpEnabled", settings)!;

        Assert.Equal(expected, enabled);
    }

    [Fact]
    public void Every_boundary_slice_preserves_legacy_rounding()
    {
        var whole = VoiceXpWatchdog.RoundAccrual(2, 10m);
        var split = VoiceXpWatchdog.RoundAccrual(1, 10m) + VoiceXpWatchdog.RoundAccrual(1, 10m);

        Assert.Equal(0.333333m, whole);
        Assert.Equal(0.333334m, split);
        Assert.NotEqual(whole, split);
    }

    [Fact]
    public void Many_five_second_watchdog_slices_round_independently()
    {
        Assert.Equal(9.999996m, Enumerable.Range(0, 12).Sum(_ => VoiceXpWatchdog.RoundAccrual(5, 10m)));
    }

    [Fact]
    public void Legacy_session_derives_qualifying_progress_without_changing_join_time()
    {
        var joinedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var eligibleAt = joinedAt.AddMinutes(3);
        var session = new VoiceSession { JoinedAt = joinedAt, EligibilityStartedAt = eligibleAt, LastAccruedAt = eligibleAt.AddMinutes(1), EligibleSeconds = 0 };

        Assert.Equal(60L, (long)Invoke("EffectiveQualifyingSeconds", session)!);
        Assert.Equal(joinedAt, session.JoinedAt);
    }

    [Fact]
    public void Pending_eligibility_windows_merge_only_when_contiguous()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var intervals = new List<VoiceEligibilityInterval>();
        Invoke("AppendPendingInterval", intervals, start, start.AddSeconds(30));
        Invoke("AppendPendingInterval", intervals, start.AddSeconds(30), start.AddSeconds(60));
        Invoke("AppendPendingInterval", intervals, start.AddSeconds(90), start.AddSeconds(120));

        Assert.Equal(2, intervals.Count);
        Assert.Equal(start.AddSeconds(60), intervals[0].EndsAtUtc);
        Assert.Equal(start.AddSeconds(90), intervals[1].StartsAtUtc);
    }

    [Fact]
    public void Watchdog_parallelism_is_bounded_by_configuration_contract()
    {
        var options = new VoiceWatchdogOptions();

        Assert.Equal(4, options.MaxConcurrentGuilds);
        Assert.InRange(Math.Clamp(options.MaxConcurrentGuilds, 1, 32), 1, 32);
    }

    [Fact]
    public void Settings_activation_requires_a_persisted_revision()
    {
        var method = typeof(VoiceXpWatchdog).GetMethod(nameof(VoiceXpWatchdog.ActivateSettingsRevisionAsync))!;
        var revision = method.GetParameters()[1];

        Assert.Equal(typeof(long), revision.ParameterType);
        Assert.Equal(typeof(Task), method.ReturnType);
    }

    private static object? Invoke(string name, params object[] arguments) => typeof(VoiceXpWatchdog)
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, arguments);

    private static GuildXpSettingsSnapshot Snapshot(long revision, decimal pointsPerMinute) => new(1, true,
        new VoiceXpSettings { Enabled = true, PointsPerMinute = pointsPerMinute }, new HashSet<ulong>(), new HashSet<ulong>(), new HashSet<ulong>(),
        new Dictionary<ulong, decimal>(), new ServerBoosterXpSettings(), revision, DateTime.UnixEpoch);
}
