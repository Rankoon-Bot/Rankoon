using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Rankoon.Data.Model;

namespace Rankoon.Data.Xp;

public sealed record SeasonScheduleCandidate(long Sequence, DateTime StartsAtUtc, DateTime EndsAtUtc, string Name, [property: JsonIgnore] int ScheduleOccurrence = 0, long? Number = null);
public sealed record SeasonSettingsValidationError(string Field, string ErrorKey);

public sealed class SeasonSettingsValidationException(IReadOnlyList<SeasonSettingsValidationError> errors)
    : ArgumentException("The season settings are invalid.", nameof(GuildSeasonSettings))
{
    public IReadOnlyList<SeasonSettingsValidationError> Errors { get; } = errors;
}

public sealed class SeasonScheduleGenerator
{
    public IReadOnlyList<SeasonScheduleCandidate> Generate(GuildSeasonSettings settings, string guildName, long firstSequence, int count, CultureInfo? culture = null, int occurrenceOffset = 0, DateTime? anchorOverrideUtc = null)
    {
        Validate(settings);
        if (settings.ScheduleKind == SeasonScheduleKind.Manual) return [];

        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var anchor = anchorOverrideUtc ?? settings.ScheduleAnchorUtc ?? throw new ArgumentException("A schedule anchor is required.", nameof(settings));
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(anchor, DateTimeKind.Utc), zone);
        var result = new List<SeasonScheduleCandidate>(count);
        for (var index = 0; index < count; index++)
        {
            var occurrence = checked(occurrenceOffset + index);
            var startLocal = AddPeriod(localStart, settings, occurrence);
            var endLocal = settings.ScheduleKind == SeasonScheduleKind.FixedDuration
                ? startLocal.AddDays(settings.FixedDurationDays!.Value)
                : AddPeriod(localStart, settings, occurrence + 1).AddDays(-settings.GapDays);
            var startsAtUtc = ToUtc(startLocal, zone);
            var endsAtUtc = ToUtc(endLocal, zone);
            if (endsAtUtc <= startsAtUtc) throw new ArgumentException("The configured season duration must be positive.", nameof(settings));
            var sequence = firstSequence + index;
            result.Add(new(sequence, startsAtUtc, endsAtUtc, SeasonNamingService.Format(settings, sequence, startsAtUtc, endsAtUtc, guildName, culture), occurrence, sequence));
        }
        return result;
    }

    public static void Validate(GuildSeasonSettings settings)
    {
        var errors = ValidateSettings(settings);
        if (errors.Count > 0) throw new SeasonSettingsValidationException(errors);
    }

    public static IReadOnlyList<SeasonSettingsValidationError> ValidateSettings(GuildSeasonSettings settings)
    {
        var errors = new List<SeasonSettingsValidationError>();
        void Invalid(string field, string errorKey = "season.invalidSchedule") => errors.Add(new(field, errorKey));

        if (settings.GuildId == 0) Invalid("guildId");
        if (!Enum.IsDefined(settings.DefaultLeaderboardScope)) Invalid("defaultLeaderboardScope");
        if (!Enum.IsDefined(settings.ScheduleKind)) Invalid("scheduleKind");
        if (!Enum.IsDefined(settings.InitialXpMode)) Invalid("initialXpMode");
        if (!Enum.IsDefined(settings.CarryOverMode)) Invalid("carryOverMode");
        if (settings.GapDays < 0) Invalid("gapDays");
        if (settings.PreparedSeasonCount is < 0 or > 24) Invalid("preparedSeasonCount");
        if (settings.PublicHistoryCount is < 0 or > 24) Invalid("publicHistoryCount");
        if (settings.WinnerCount is < 1 or > 100) Invalid("winnerCount");
        if (settings.InitialXpPercentage is < 0 or > 100) Invalid("initialXpPercentage");
        if (settings.CarryOverPercentage is < 0 or > 100) Invalid("carryOverPercentage");
        if (settings.CarryOverMaximumXp is < 0) Invalid("carryOverMaximumXp");
        if (settings.AnnouncementChannelId == 0) Invalid("announcementChannelId");
        if (!string.Equals(settings.PauseBehavior, "NoSeasonXp", StringComparison.Ordinal)) Invalid("pauseBehavior");

        if (settings.FixedDurationDays is not null && settings.FixedDurationDays is not (>= 1 and <= 3660)) Invalid("fixedDurationDays");
        if (settings.ScheduleKind == SeasonScheduleKind.FixedDuration && settings.FixedDurationDays is null) Invalid("fixedDurationDays");
        if (settings.ScheduleKind != SeasonScheduleKind.Manual && settings.ScheduleAnchorUtc is null) Invalid("scheduleAnchorUtc");
        if (settings.ScheduleKind is SeasonScheduleKind.Monthly or SeasonScheduleKind.Quarterly or SeasonScheduleKind.SemiAnnual or SeasonScheduleKind.Annual &&
            settings.GapDays >= MonthsPerPeriod(settings.ScheduleKind) * 28)
            Invalid("gapDays");

        if (string.IsNullOrWhiteSpace(settings.TimeZoneId)) Invalid("timeZoneId", "season.invalidTimeZone");
        else
        {
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId); }
            catch (TimeZoneNotFoundException) { Invalid("timeZoneId", "season.invalidTimeZone"); }
            catch (InvalidTimeZoneException) { Invalid("timeZoneId", "season.invalidTimeZone"); }
        }

        if (settings.Rotation == null) Invalid("rotation");
        else
        {
            if (settings.Rotation.Any(name => string.IsNullOrWhiteSpace(name) || name != name.Trim())) Invalid("rotation");
            if (settings.Rotation.Where(name => name != null).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.Rotation.Count) Invalid("rotation");
        }

        try { SeasonNamingService.Validate(settings.NameTemplate, settings.Rotation); }
        catch (ArgumentException exception) { Invalid(exception.ParamName == "rotation" ? "rotation" : "nameTemplate"); }

        if (settings.Announcements == null) Invalid("announcements");
        else if (settings.Announcements.WarningOffsetsMinutes == null) Invalid("announcements.warningOffsetsMinutes");
        else
        {
            var warningOffsets = settings.Announcements.WarningOffsetsMinutes;
            if (warningOffsets.Any(offset => offset < 0) || warningOffsets.Distinct().Count() != warningOffsets.Count) Invalid("announcements.warningOffsetsMinutes");
        }

        if (settings.SeasonLevelRoles == null) Invalid("seasonLevelRoles");
        else
        {
            if (settings.SeasonLevelRoles.Any(role => role == null || role.Level <= 0 || role.RoleId == 0 || !Enum.IsDefined(role.Retention))) Invalid("seasonLevelRoles");
            if (settings.SeasonLevelRoles.Where(role => role != null).GroupBy(role => role.Level).Any(group => group.Count() > 1) ||
                settings.SeasonLevelRoles.Where(role => role != null).GroupBy(role => role.RoleId).Any(group => group.Count() > 1)) Invalid("seasonLevelRoles");
        }

        return errors;
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

public static class SeasonSchedulePlanner
{
    private const int BatchSize = 120;
    private const int MaximumOccurrences = 36_600;

    public static IReadOnlyList<SeasonScheduleCandidate> GenerateMissing(GuildSeasonSettings settings, IReadOnlyCollection<GuildSeason> existing, int desiredPreparedCount, DateTime notEndedAfterUtc)
    {
        var missing = desiredPreparedCount - SeasonCoordinator.CountPrepared(existing, notEndedAfterUtc);
        if (missing <= 0 || settings.ScheduleKind == SeasonScheduleKind.Manual) return [];

        var firstSequence = Math.Max(existing.Select(x => x.Sequence + 1).DefaultIfEmpty(1).Max(), settings.NextSequenceAfterDeletion);
        var prepared = existing.Where(x => x.Status is SeasonStatus.Scheduled or SeasonStatus.Active or SeasonStatus.Closing && x.EndsAtUtc > notEndedAfterUtc).ToList();
        var latestPrepared = prepared.OrderByDescending(x => x.EndsAtUtc).ThenByDescending(x => x.Sequence).FirstOrDefault();
        DateTime? continuationAnchor = latestPrepared == null ? null : latestPrepared.EndsAtUtc.AddDays(settings.GapDays);
        var firstNumber = NextNumber(settings, existing);
        var selected = new List<SeasonScheduleCandidate>(missing);
        var generator = new SeasonScheduleGenerator();

        for (var offset = 0; offset < MaximumOccurrences && selected.Count < missing; offset += BatchSize)
        {
            var batch = generator.Generate(settings, "Guild", 1, Math.Min(BatchSize, MaximumOccurrences - offset), occurrenceOffset: offset, anchorOverrideUtc: continuationAnchor);
            foreach (var candidate in batch)
            {
                if (candidate.EndsAtUtc <= notEndedAfterUtc) continue;
                if (prepared.Any(season => season.StartsAtUtc < candidate.EndsAtUtc && candidate.StartsAtUtc < season.EndsAtUtc)) continue;
                var sequence = firstSequence + selected.Count;
                var number = firstNumber + selected.Count;
                selected.Add(candidate with { Sequence = sequence, Number = number, Name = SeasonNamingService.Format(settings, number, candidate.StartsAtUtc, candidate.EndsAtUtc, "Guild") });
                if (selected.Count == missing) break;
            }
        }

        return selected;
    }

    public static long EffectiveNumber(GuildSeason season) => season.Number ?? season.Sequence;

    public static long NextNumber(GuildSeasonSettings settings, IEnumerable<GuildSeason> seasons)
    {
        var relevant = seasons.Where(x => x.NumberingEpoch == settings.NumberingEpoch).ToList();
        return NextCompletedNumber(settings, relevant) + relevant.LongCount(x => x.Status is SeasonStatus.Scheduled or SeasonStatus.Active or SeasonStatus.Closing);
    }

    public static long NextCompletedNumber(GuildSeasonSettings settings, IEnumerable<GuildSeason> seasons) =>
        seasons.LongCount(x => x.NumberingEpoch == settings.NumberingEpoch && x.Status == SeasonStatus.Closed && x.Finalized) + 1;
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
                "rotation" => settings.Rotation[(int)(((sequence - 1 + settings.RotationOffset) % settings.Rotation.Count + settings.Rotation.Count) % settings.Rotation.Count)],
                _ when token.StartsWith("start:", StringComparison.Ordinal) => start.ToString(token[6..], culture),
                _ when token.StartsWith("end:", StringComparison.Ordinal) => end.ToString(token[4..], culture),
                _ => throw new ArgumentException($"Unknown season name token '{token}'.", nameof(settings))
            };
        });
    }
}
