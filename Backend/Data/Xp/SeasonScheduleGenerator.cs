using System.Globalization;
using System.Text.RegularExpressions;
using Rankoon.Data.Model;

namespace Rankoon.Data.Xp;

public sealed record SeasonScheduleCandidate(long Sequence, DateTime StartsAtUtc, DateTime EndsAtUtc, string Name);

public sealed class SeasonScheduleGenerator
{
    public IReadOnlyList<SeasonScheduleCandidate> Generate(GuildSeasonSettings settings, string guildName, long firstSequence, int count, CultureInfo? culture = null)
    {
        Validate(settings);
        if (settings.ScheduleKind == SeasonScheduleKind.Manual) return [];

        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var anchor = settings.ScheduleAnchorUtc ?? throw new ArgumentException("A schedule anchor is required.", nameof(settings));
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(anchor, DateTimeKind.Utc), zone);
        var result = new List<SeasonScheduleCandidate>(count);
        for (var index = 0; index < count; index++)
        {
            var startLocal = AddPeriod(localStart, settings, index);
            var endLocal = settings.ScheduleKind == SeasonScheduleKind.FixedDuration
                ? startLocal.AddDays(settings.FixedDurationDays!.Value)
                : AddPeriod(localStart, settings, index + 1).AddDays(-settings.GapDays);
            var startsAtUtc = ToUtc(startLocal, zone);
            var endsAtUtc = ToUtc(endLocal, zone);
            if (endsAtUtc <= startsAtUtc) throw new ArgumentException("The configured season duration must be positive.", nameof(settings));
            var sequence = firstSequence + index;
            result.Add(new(sequence, startsAtUtc, endsAtUtc, SeasonNamingService.Format(settings, sequence, startsAtUtc, endsAtUtc, guildName, culture)));
        }
        return result;
    }

    public static void Validate(GuildSeasonSettings settings)
    {
        if (!Enum.IsDefined(settings.ScheduleKind)) throw new ArgumentException("Unknown schedule kind.", nameof(settings));
        if (settings.GapDays < 0) throw new ArgumentException("The gap cannot be negative.", nameof(settings));
        if (settings.PreparedSeasonCount is < 0 or > 24) throw new ArgumentException("Prepared season count must be between 0 and 24.", nameof(settings));
        if (settings.ScheduleKind != SeasonScheduleKind.Manual && settings.ScheduleAnchorUtc is null) throw new ArgumentException("A schedule anchor is required.", nameof(settings));
        if (settings.ScheduleKind == SeasonScheduleKind.FixedDuration && settings.FixedDurationDays is not (> 0 and <= 3660)) throw new ArgumentException("Fixed duration must be between 1 and 3660 days.", nameof(settings));
        if (settings.ScheduleKind is SeasonScheduleKind.Monthly or SeasonScheduleKind.Quarterly or SeasonScheduleKind.SemiAnnual or SeasonScheduleKind.Annual && settings.GapDays >= MonthsPerPeriod(settings.ScheduleKind) * 28)
            throw new ArgumentException("The gap is longer than the calendar period.", nameof(settings));
        _ = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        SeasonNamingService.Validate(settings.NameTemplate, settings.Rotation);
    }

    private static DateTime AddPeriod(DateTime anchor, GuildSeasonSettings settings, int index) => settings.ScheduleKind switch
    {
        SeasonScheduleKind.FixedDuration => anchor.AddDays(index * (settings.FixedDurationDays!.Value + settings.GapDays)),
        SeasonScheduleKind.Monthly => anchor.AddMonths(index),
        SeasonScheduleKind.Quarterly => anchor.AddMonths(index * 3),
        SeasonScheduleKind.SemiAnnual => anchor.AddMonths(index * 6),
        SeasonScheduleKind.Annual => anchor.AddYears(index),
        _ => throw new ArgumentOutOfRangeException(nameof(settings))
    };

    private static int MonthsPerPeriod(SeasonScheduleKind kind) => kind switch
    {
        SeasonScheduleKind.Monthly => 1,
        SeasonScheduleKind.Quarterly => 3,
        SeasonScheduleKind.SemiAnnual => 6,
        SeasonScheduleKind.Annual => 12,
        _ => 0
    };

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
        {
            // Choose the earlier UTC instant consistently when the wall clock repeats.
            return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime;
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}

public static class SeasonNamingService
{
    private static readonly Regex Token = new("\\{(?<token>[^{}]+)\\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void Validate(string? template, IReadOnlyList<string>? rotation)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 120) throw new ArgumentException("A season name template is required.", nameof(template));
        foreach (Match match in Token.Matches(template))
        {
            var token = match.Groups["token"].Value;
            if (token == "rotation" && (rotation == null || rotation.Count == 0 || rotation.Any(string.IsNullOrWhiteSpace))) throw new ArgumentException("Rotation requires at least one non-empty name.", nameof(rotation));
            if (token is "number" or "year" or "endYear" or "month" or "monthName" or "quarter" or "rotation" || Regex.IsMatch(token, "^number:0+$", RegexOptions.CultureInvariant) || Regex.IsMatch(token, "^(start|end):[yMd-]+$", RegexOptions.CultureInvariant)) continue;
            throw new ArgumentException($"Unknown season name token '{token}'.", nameof(template));
        }
    }

    public static string Format(GuildSeasonSettings settings, long sequence, DateTime startsAtUtc, DateTime endsAtUtc, string guildName, CultureInfo? culture = null)
    {
        Validate(settings.NameTemplate, settings.Rotation);
        culture ??= CultureInfo.GetCultureInfo("en-US");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startsAtUtc, DateTimeKind.Utc), zone);
        var end = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(endsAtUtc, DateTimeKind.Utc), zone);
        return Token.Replace(settings.NameTemplate, match =>
        {
            var token = match.Groups["token"].Value;
            if (token == "number") return sequence.ToString(culture);
            if (token.StartsWith("number:", StringComparison.Ordinal)) return sequence.ToString(token[7..], culture);
            return token switch
            {
                "year" => start.Year.ToString(culture),
                "endYear" => end.Year.ToString(culture),
                "month" => start.Month.ToString(culture),
                "monthName" => culture.DateTimeFormat.GetMonthName(start.Month),
                "quarter" => ((start.Month - 1) / 3 + 1).ToString(culture),
                "rotation" => settings.Rotation[Math.Abs((int)((sequence - 1 + settings.RotationOffset) % settings.Rotation.Count))],
                _ when token.StartsWith("start:", StringComparison.Ordinal) => start.ToString(token[6..], culture),
                _ when token.StartsWith("end:", StringComparison.Ordinal) => end.ToString(token[4..], culture),
                _ => throw new ArgumentException($"Unknown season name token '{token}'.", nameof(settings))
            };
        });
    }
}
