using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class XpProjectionTests
{
    [Fact]
    public void Xp_ledger_projection_state_is_bounded_to_the_ledger_entry()
    {
        var ledger = new XpLedgerEntry { ProjectionLeaseOwner = "worker", ProjectionLeaseExpiresAtUtc = DateTime.UnixEpoch };
        var member = new MemberXp();
        var stats = new GuildStats();

        var ledgerDocument = ledger.ToBsonDocument();
        Assert.Equal("worker", ledgerDocument["projection_lease_owner"].AsString);
        Assert.False(member.ToBsonDocument().Contains("applied_ledger_keys"));
        Assert.False(stats.ToBsonDocument().Contains("applied_ledger_keys"));
    }

    [Fact]
    public void Voice_seconds_only_count_valid_voice_ledger_intervals()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var voice = new XpLedgerEntry { Source = "voice", PeriodStartsAtUtc = start, PeriodEndsAtUtc = start.AddSeconds(90) };
        var message = new XpLedgerEntry { Source = "message", PeriodStartsAtUtc = start, PeriodEndsAtUtc = start.AddSeconds(90) };

        Assert.Equal(90L, (long)Invoke("VoiceSeconds", voice)!);
        Assert.Equal(0L, (long)Invoke("VoiceSeconds", message)!);
    }

    [Fact]
    public void Cooldown_fields_are_persisted_on_ledger_and_member_documents()
    {
        var ledger = new XpLedgerEntry { CooldownSource = "reaction", CooldownSeconds = 30, CooldownAcquired = true, IsProjectionControl = true };
        var member = new MemberXp { LastReactionXpAt = DateTime.UnixEpoch, LastReactionXpGrantKey = "reaction:1:2:a" };

        var ledgerDocument = ledger.ToBsonDocument();
        var memberDocument = member.ToBsonDocument();

        Assert.Equal("reaction", ledgerDocument["cooldown_source"].AsString);
        Assert.Equal(30, ledgerDocument["cooldown_seconds"].ToInt32());
        Assert.True(ledgerDocument["cooldown_acquired"].AsBoolean);
        Assert.True(ledgerDocument["is_projection_control"].AsBoolean);
        Assert.Equal("reaction:1:2:a", memberDocument["last_reaction_xp_grant_key"].AsString);
    }

    [Fact]
    public void Only_automatic_event_interest_grants_can_be_reversed_as_event_interest()
    {
        var automaticGrant = new XpLedgerEntry { Source = "event_interest", Kind = XpLedgerEntryKind.AutomaticGrant };
        var manualAdjustment = new XpLedgerEntry { Source = "event_interest", Kind = XpLedgerEntryKind.ManualAdjustment };
        var otherAutomaticGrant = new XpLedgerEntry { Source = "reaction", Kind = XpLedgerEntryKind.AutomaticGrant };

        Assert.True(XpService.MatchesAutomaticGrant(automaticGrant, "event_interest"));
        Assert.False(XpService.MatchesAutomaticGrant(manualAdjustment, "event_interest"));
        Assert.False(XpService.MatchesAutomaticGrant(otherAutomaticGrant, "event_interest"));
    }

    private static object? Invoke(string name, params object[] arguments) => typeof(XpService)
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, arguments);
}
