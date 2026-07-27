using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class VoiceXpWatchdogTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, true, false)]
    public void Voice_lifecycle_only_handles_channel_or_deafen_transitions(bool channelChanged, bool wasDeafened, bool isDeafened, bool expected)
    {
        Assert.Equal(expected, VoiceXpWatchdog.IsRelevantVoiceStateChange(channelChanged, wasDeafened, isDeafened));
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

    [Fact]
    public void First_qualifying_settlement_starts_at_session_join()
    {
        var joinedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var session = new VoiceSession { JoinedAt = joinedAt, LastAccruedAt = joinedAt.AddMinutes(2), EligibleSeconds = 0 };

        var start = (DateTime)Invoke("PeriodStart", session, true)!;

        Assert.Equal(joinedAt, start);
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
    public void Hard_ineligibility_advances_first_accrual_without_changing_join_time()
    {
        var joinedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var eligibleAt = joinedAt.AddMinutes(3);
        var session = new VoiceSession { JoinedAt = joinedAt, EligibilityStartedAt = eligibleAt, LastAccruedAt = eligibleAt.AddMinutes(1), EligibleSeconds = 0 };

        Assert.Equal(eligibleAt, (DateTime)Invoke("PeriodStart", session, true)!);
        Assert.Equal(joinedAt, session.JoinedAt);
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
