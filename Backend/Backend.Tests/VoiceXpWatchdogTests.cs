using System.Reflection;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Xunit;

namespace Backend.Tests;

public sealed class VoiceXpWatchdogTests
{
    [Fact]
    public void Season_boundaries_split_the_entire_accrual_interval()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMinutes(10);
        var segments = (IReadOnlyList<(DateTime Start, DateTime End)>)Invoke("CreateSegments", new[] { start.AddMinutes(4), start.AddMinutes(7) }, start, end)!;

        Assert.Equal(new[] { (start, start.AddMinutes(4)), (start.AddMinutes(4), start.AddMinutes(7)), (start.AddMinutes(7), end) }, segments);
    }

    [Fact]
    public void Grant_key_is_deterministic_for_a_voice_interval()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMinutes(5);

        var first = (string)Invoke("VoiceGrantKey", 1UL, 2UL, 3UL, start, end)!;
        var retry = (string)Invoke("VoiceGrantKey", 1UL, 2UL, 3UL, start, end)!;

        Assert.Equal("voice:1:2:3:639028224000000000:639028227000000000", first);
        Assert.Equal(first, retry);
    }

    [Fact]
    public void First_qualifying_settlement_starts_at_session_join()
    {
        var joinedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var session = new VoiceSession { JoinedAt = joinedAt, LastAccruedAt = joinedAt.AddMinutes(2), EligibleSeconds = 0 };

        var start = (DateTime)Invoke("PeriodStart", session, true)!;

        Assert.Equal(joinedAt, start);
    }

    private static object? Invoke(string name, params object[] arguments) => typeof(VoiceXpWatchdog)
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, arguments);
}
