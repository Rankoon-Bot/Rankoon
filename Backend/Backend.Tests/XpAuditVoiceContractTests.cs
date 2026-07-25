using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Rankoon.Controllers;
using Rankoon.Data.Xp;
using Rankoon.Data.Model;
using Xunit;

namespace Backend.Tests;

public sealed class XpAuditVoiceContractTests
{
    [Fact]
    public void SegmentEndpointUsesRequestedRoute()
    {
        var method = typeof(XpAuditController).GetMethod(nameof(XpAuditController.VoiceDaySegments));
        var route = method!.GetCustomAttribute<HttpGetAttribute>();

        Assert.Equal("members/{userId}/voice-days/{dayKey}/segments", route!.Template);
    }

    [Fact]
    public void SegmentDtoDoesNotExposeStorageOrSessionFields()
    {
        var dto = new XpAuditVoiceSegmentItem("opaque", DateTime.UnixEpoch, DateTime.UnixEpoch.AddMinutes(1), 60, 2m, 3, null, null, 2m, 1m, null);
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"effectiveXpPerMinute\"", json);
        Assert.Contains("\"channelMultiplier\"", json);
        Assert.Contains("\"serverBoosterMultiplier\"", json);
        Assert.DoesNotContain("sessionId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("settings", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("part", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grantKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Voice_day_summary_aggregates_only_segments_matching_channel_and_season()
    {
        var start = new DateTime(2026, 7, 24, 10, 0, 0, DateTimeKind.Utc);
        var matching = new XpAuditService.VoiceSegment("a", "2026-07-24", "one", start, start.AddMinutes(1), 60, 5m, 42, "season-a", XpLedgerScope.LifetimeAndSeason, SeasonProjectionStatus.Applied, 5m, 1m, null);
        var otherChannel = matching with { Id = "b", ChannelId = 99, AwardedXp = 7m };
        var otherSeason = matching with { Id = "c", SeasonId = "season-b", AwardedXp = 11m };
        var filter = new XpAuditEntryFilter("voice", null, null, "season-a", null, null, null, null, null, 42);

        var selected = new[] { matching, otherChannel, otherSeason }.Where(x => XpAuditService.MatchesVoiceFilter(x, filter));
        var day = Assert.Single(XpAuditService.BuildVoiceDays(selected, new Dictionary<string, string> { ["season-a"] = "A" }));

        Assert.Equal(5m, day.TotalAmount);
        Assert.Equal(1, day.EntryCount);
        Assert.Equal(42UL, day.ChannelId);
        Assert.Equal("season-a", day.SeasonId);
    }
}
