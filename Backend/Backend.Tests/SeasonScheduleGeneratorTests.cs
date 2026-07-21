using System.Globalization;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class SeasonScheduleGeneratorTests
{
    [Fact]
    public void Generates_monthly_periods_and_localized_rotation_names()
    {
        var settings = Settings(SeasonScheduleKind.Monthly, "Europe/Berlin", new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        settings.NameTemplate = "{rotation} {monthName} {number:00}";
        settings.Rotation = ["Fruehling", "Sommer"];
        var seasons = new SeasonScheduleGenerator().Generate(settings, "Guild", 1, 2, CultureInfo.GetCultureInfo("de-DE"));
        Assert.Equal("Fruehling Januar 01", seasons[0].Name);
        Assert.Equal("Sommer Februar 02", seasons[1].Name);
        Assert.Equal(new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc), seasons[0].EndsAtUtc);
    }

    [Fact]
    public void Generates_quarterly_and_annual_periods_across_leap_year()
    {
        var quarterly = Settings(SeasonScheduleKind.Quarterly, "UTC", new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Utc));
        var annual = Settings(SeasonScheduleKind.Annual, "UTC", new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Utc));
        var generator = new SeasonScheduleGenerator();
        Assert.Equal(new DateTime(2024, 5, 29, 0, 0, 0, DateTimeKind.Utc), generator.Generate(quarterly, "Guild", 1, 2)[0].EndsAtUtc);
        Assert.Equal(new DateTime(2025, 2, 28, 0, 0, 0, DateTimeKind.Utc), generator.Generate(annual, "Guild", 1, 2)[0].EndsAtUtc);
    }

    [Fact]
    public void Fixed_duration_applies_gap_without_overlap()
    {
        var settings = Settings(SeasonScheduleKind.FixedDuration, "UTC", new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        settings.FixedDurationDays = 7;
        settings.GapDays = 2;
        var seasons = new SeasonScheduleGenerator().Generate(settings, "Guild", 4, 2);
        Assert.Equal(new DateTime(2027, 1, 8, 0, 0, 0, DateTimeKind.Utc), seasons[0].EndsAtUtc);
        Assert.Equal(new DateTime(2027, 1, 10, 0, 0, 0, DateTimeKind.Utc), seasons[1].StartsAtUtc);
    }

    [Fact]
    public void Generates_half_open_calendar_intervals_with_sequence_based_names()
    {
        var settings = Settings(SeasonScheduleKind.Monthly, "UTC", new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        settings.GapDays = 2;
        settings.NameTemplate = "Season {number}";
        var seasons = new SeasonScheduleGenerator().Generate(settings, "Guild", 8, 2);
        Assert.Equal("Season 8", seasons[0].Name);
        Assert.Equal(seasons[0].EndsAtUtc, new DateTime(2027, 1, 30, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(seasons[0].EndsAtUtc <= seasons[1].StartsAtUtc);
    }

    [Fact]
    public void Rejects_unknown_tokens_and_empty_rotation()
    {
        var settings = Settings(SeasonScheduleKind.Monthly, "UTC", DateTime.UnixEpoch);
        settings.NameTemplate = "{unknown}";
        Assert.Throws<ArgumentException>(() => SeasonScheduleGenerator.Validate(settings));
        settings.NameTemplate = "{rotation}";
        Assert.Throws<ArgumentException>(() => SeasonScheduleGenerator.Validate(settings));
    }

    private static GuildSeasonSettings Settings(SeasonScheduleKind kind, string timeZoneId, DateTime anchor) => new()
    {
        ScheduleKind = kind,
        TimeZoneId = timeZoneId,
        ScheduleAnchorUtc = anchor,
        FixedDurationDays = 7,
        NameTemplate = "Season {number}"
    };
}
