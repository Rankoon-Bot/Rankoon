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
        Assert.Contains(Assert.Throws<SeasonSettingsValidationException>(() => SeasonScheduleGenerator.Validate(settings)).Errors, error => error.Field == "nameTemplate");
        settings.NameTemplate = "{rotation}";
        Assert.Contains(Assert.Throws<SeasonSettingsValidationException>(() => SeasonScheduleGenerator.Validate(settings)).Errors, error => error.Field == "rotation");
    }

    [Fact]
    public void Rejects_duplicate_rotation_names_case_insensitively()
    {
        var settings = Settings(SeasonScheduleKind.Monthly, "UTC", DateTime.UnixEpoch);
        settings.Rotation = ["Spring", "spring"];

        var errors = SeasonScheduleGenerator.ValidateSettings(settings);

        Assert.Contains(errors, error => error.Field == "rotation");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Summer")]
    [InlineData("Summer ")]
    public void Rejects_empty_or_untrimmed_rotation_names(string name)
    {
        var settings = Settings(SeasonScheduleKind.Monthly, "UTC", DateTime.UnixEpoch);
        settings.Rotation = [name];

        Assert.Contains(SeasonScheduleGenerator.ValidateSettings(settings), error => error.Field == "rotation");
    }

    [Fact]
    public void Rejects_null_nested_settings_and_collections()
    {
        var settings = Settings(SeasonScheduleKind.Monthly, "UTC", DateTime.UnixEpoch);
        settings.Announcements = null!;
        settings.Rotation = null!;
        settings.SeasonLevelRoles = null!;

        var fields = SeasonScheduleGenerator.ValidateSettings(settings).Select(error => error.Field).ToHashSet();

        Assert.Contains("announcements", fields);
        Assert.Contains("rotation", fields);
        Assert.Contains("seasonLevelRoles", fields);

        settings.Announcements = new SeasonAnnouncementSettings { WarningOffsetsMinutes = null! };
        Assert.Contains(SeasonScheduleGenerator.ValidateSettings(settings), error => error.Field == "announcements.warningOffsetsMinutes");
    }

    [Fact]
    public void Rejects_invalid_editable_ranges_and_enums()
    {
        var settings = Settings(SeasonScheduleKind.FixedDuration, "UTC", DateTime.UnixEpoch);
        settings.DefaultLeaderboardScope = (SeasonLeaderboardScope)99;
        settings.InitialXpMode = (SeasonInitialXpMode)99;
        settings.CarryOverMode = (SeasonCarryOverMode)99;
        settings.FixedDurationDays = 3661;
        settings.GapDays = -1;
        settings.PreparedSeasonCount = 25;
        settings.PublicHistoryCount = 25;
        settings.WinnerCount = 101;
        settings.InitialXpPercentage = 100.01m;
        settings.CarryOverPercentage = -0.01m;
        settings.CarryOverMaximumXp = -1;

        var fields = SeasonScheduleGenerator.ValidateSettings(settings).Select(error => error.Field).ToHashSet();

        Assert.Contains("defaultLeaderboardScope", fields);
        Assert.Contains("initialXpMode", fields);
        Assert.Contains("carryOverMode", fields);
        Assert.Contains("fixedDurationDays", fields);
        Assert.Contains("gapDays", fields);
        Assert.Contains("preparedSeasonCount", fields);
        Assert.Contains("publicHistoryCount", fields);
        Assert.Contains("winnerCount", fields);
        Assert.Contains("initialXpPercentage", fields);
        Assert.Contains("carryOverPercentage", fields);
        Assert.Contains("carryOverMaximumXp", fields);
    }

    [Fact]
    public void Rejects_calendar_gap_and_invalid_role_and_warning_lists()
    {
        var settings = Settings(SeasonScheduleKind.Monthly, "UTC", DateTime.UnixEpoch);
        settings.GapDays = 28;
        settings.Announcements.WarningOffsetsMinutes = [60, 60, -1];
        settings.SeasonLevelRoles =
        [
            new SeasonLevelRole { Level = 0, RoleId = 1, Retention = (SeasonLevelRoleRetention)99 },
            new SeasonLevelRole { Level = 0, RoleId = 1 }
        ];

        var fields = SeasonScheduleGenerator.ValidateSettings(settings).Select(error => error.Field).ToHashSet();

        Assert.Contains("gapDays", fields);
        Assert.Contains("announcements.warningOffsetsMinutes", fields);
        Assert.Contains("seasonLevelRoles", fields);
    }

    [Fact]
    public void Rejects_zero_guild_and_invalid_time_zone_with_field_errors()
    {
        var settings = Settings(SeasonScheduleKind.Monthly, "not/a-time-zone", DateTime.UnixEpoch);
        settings.GuildId = 0;

        var errors = SeasonScheduleGenerator.ValidateSettings(settings);

        Assert.Contains(errors, error => error.Field == "guildId");
        Assert.Contains(errors, error => error.Field == "timeZoneId" && error.ErrorKey == "season.invalidTimeZone");
    }

    [Fact]
    public void Normalizes_negative_rotation_offsets()
    {
        var settings = Settings(SeasonScheduleKind.Monthly, "UTC", DateTime.UnixEpoch);
        settings.NameTemplate = "{rotation}";
        settings.Rotation = ["Spring", "Summer", "Autumn"];
        settings.RotationOffset = -1;

        var generated = new SeasonScheduleGenerator().Generate(settings, "Guild", 1, 1);

        Assert.Equal("Autumn", generated[0].Name);
    }

    [Fact]
    public void Generates_a_later_occurrence_batch_without_restarting_at_the_anchor()
    {
        var settings = Settings(SeasonScheduleKind.FixedDuration, "UTC", DateTime.UnixEpoch);
        settings.FixedDurationDays = 30;

        var generated = new SeasonScheduleGenerator().Generate(settings, "Guild", 121, 1, occurrenceOffset: 120);

        Assert.Equal(121, generated[0].Sequence);
        Assert.Equal(DateTime.UnixEpoch.AddDays(3600), generated[0].StartsAtUtc);
    }

    [Fact]
    public void Active_season_counts_towards_the_prepared_total()
    {
        var seasons = new[]
        {
            new GuildSeason { Status = SeasonStatus.Active, Sequence = 1 },
            new GuildSeason { Status = SeasonStatus.Scheduled, Sequence = 2 },
            new GuildSeason { Status = SeasonStatus.Scheduled, Sequence = 3 },
            new GuildSeason { Status = SeasonStatus.Scheduled, Sequence = 4 },
            new GuildSeason { Status = SeasonStatus.Scheduled, Sequence = 5 },
            new GuildSeason { Status = SeasonStatus.Scheduled, Sequence = 6 },
            new GuildSeason { Status = SeasonStatus.Cancelled, Sequence = 7 }
        };

        Assert.Equal(6, SeasonCoordinator.CountPrepared(seasons));
    }

    [Fact]
    public void Fresh_six_season_schedule_is_numbered_one_through_six()
    {
        var settings = Settings(SeasonScheduleKind.FixedDuration, "UTC", DateTime.UnixEpoch);
        settings.FixedDurationDays = 30;

        var generated = new SeasonScheduleGenerator().Generate(settings, "Guild", 1, 6);

        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, generated.Select(x => x.Sequence));
        Assert.Equal(new[] { "Season 1", "Season 2", "Season 3", "Season 4", "Season 5", "Season 6" }, generated.Select(x => x.Name));
    }

    private static GuildSeasonSettings Settings(SeasonScheduleKind kind, string timeZoneId, DateTime anchor) => new()
    {
        GuildId = 1,
        ScheduleKind = kind,
        TimeZoneId = timeZoneId,
        ScheduleAnchorUtc = anchor,
        FixedDurationDays = 7,
        NameTemplate = "Season {number}"
    };
}
