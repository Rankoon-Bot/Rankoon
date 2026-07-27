using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Xp;

public sealed record VoiceActivityInterval(DateTime StartsAtUtc, DateTime EndsAtUtc);

public sealed record VoiceAccrualSlice(
    ulong GuildId,
    ulong UserId,
    string SessionId,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    ulong ChannelId,
    string? SeasonId,
    long EligibleSeconds,
    decimal AwardedXp,
    decimal EffectiveXpPerMinute,
    decimal ChannelMultiplier,
    decimal? ServerBoosterMultiplier,
    long SettingsRevision,
    DateTime UpdatedAtUtc,
    [property: MongoDB.Bson.Serialization.Attributes.BsonIgnore] string DisplayName = "",
    bool AlreadyProjected = false);

public sealed record VoiceAccrualResult(bool Applied, bool Duplicate, bool PartialOverlap, bool GapDetected, DateTime ProcessedThroughUtc, int Part, long Revision);

public sealed record VoiceAccrualPlan(bool Duplicate, bool PartialOverlap, bool GapDetected, bool RollOver, VoiceActivitySegment? Segment, VoiceActivityDay? Replacement);

public interface IVoiceActivityAccumulator
{
    Task<VoiceAccrualResult> AccrueAsync(VoiceAccrualSlice slice, CancellationToken cancellationToken = default);
}

public sealed class VoiceActivityAccumulator(RankoonDbContext database, IOptions<VoiceActivityOptions> configuredOptions, ILogger<VoiceActivityAccumulator> logger) : IVoiceActivityAccumulator
{
    public const int MaximumSegmentsPerPart = 2000;
    private readonly VoiceActivityOptions options = configuredOptions.Value;

    public async Task<VoiceAccrualResult> AccrueAsync(VoiceAccrualSlice input, CancellationToken cancellationToken = default)
    {
        var slice = Normalize(input);
        Validate(slice);
        var dayStart = slice.StartsAtUtc.Date;
        for (var attempt = 0; attempt < options.MaxWriteAttempts; attempt++)
        {
            var current = await database.VoiceActivities.Find(x => x.GuildId == slice.GuildId && x.UserId == slice.UserId && x.DayStartUtc == dayStart && x.SessionCursors.Any(c => c.SessionId == slice.SessionId)).SortByDescending(x => x.Part).FirstOrDefaultAsync(cancellationToken)
                ?? await database.VoiceActivities.Find(x => x.GuildId == slice.GuildId && x.UserId == slice.UserId && x.DayStartUtc == dayStart).SortByDescending(x => x.Part).FirstOrDefaultAsync(cancellationToken);
            var plan = Plan(current, slice, Math.Min(options.MaximumSegmentsPerDocument, MaximumSegmentsPerPart));
            if (plan.Duplicate)
                return new(false, true, plan.PartialOverlap, plan.GapDetected, current!.SessionCursors.Single(x => x.SessionId == slice.SessionId).ProcessedThroughUtc, current.Part, current.Revision);

            var replacement = plan.Replacement!;
            if (plan.RollOver || current == null)
            {
                try
                {
                    await database.VoiceActivities.InsertOneAsync(replacement, cancellationToken: cancellationToken);
                    LogGap(slice, plan);
                    return new(true, false, plan.PartialOverlap, plan.GapDetected, slice.EndsAtUtc, replacement.Part, replacement.Revision);
                }
                catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
            }
            else
            {
                var result = await database.VoiceActivities.ReplaceOneAsync(x => x.Id == current.Id && x.Revision == current.Revision, replacement, cancellationToken: cancellationToken);
                if (result.ModifiedCount == 1)
                {
                    LogGap(slice, plan);
                    return new(true, false, plan.PartialOverlap, plan.GapDetected, slice.EndsAtUtc, replacement.Part, replacement.Revision);
                }
            }
        }
        throw new InvalidOperationException($"Voice accrual exceeded {options.MaxWriteAttempts} CAS attempts for session '{slice.SessionId}'.");
    }

    private void LogGap(VoiceAccrualSlice slice, VoiceAccrualPlan plan)
    {
        if (plan.GapDetected) logger.LogWarning("Voice session {SessionId} has an accrual gap before {StartsAtUtc}", slice.SessionId, slice.StartsAtUtc);
    }

    public static VoiceAccrualPlan Plan(VoiceActivityDay? current, VoiceAccrualSlice input, int maxSegmentsPerPart = MaximumSegmentsPerPart)
    {
        var cursor = current?.SessionCursors.SingleOrDefault(x => x.SessionId == input.SessionId);
        if (cursor != null && input.EndsAtUtc <= cursor.ProcessedThroughUtc) return new(true, false, false, false, null, null);

        var partial = cursor != null && input.StartsAtUtc < cursor.ProcessedThroughUtc;
        var gap = cursor != null && input.StartsAtUtc > cursor.ProcessedThroughUtc;
        var segment = ToSegment(input, partial ? cursor!.ProcessedThroughUtc : input.StartsAtUtc);
        var sourceSegments = current?.Segments ?? [];
        var merged = Merge(sourceSegments.Append(segment));
        var rollOver = current != null && merged.Count > maxSegmentsPerPart;
        if (input.AlreadyProjected && current?.ProjectionStatus == VoiceActivityProjectionStatus.Projecting)
            throw new InvalidOperationException("Legacy migration cannot modify a day while its projection is active.");

        var replacement = rollOver || current == null
            ? NewDay(input, current?.Part + 1 ?? 0, segment)
            : Copy(current);
        if (!rollOver && current != null) replacement.Segments = merged;
        if (rollOver && current != null)
        {
            // Keep every durable session checkpoint so retries can find the newest part.
            replacement.SessionCursors = current.SessionCursors
                .Select(x => new VoiceSessionCursor { SessionId = x.SessionId, ProcessedThroughUtc = x.ProcessedThroughUtc })
                .ToList();
        }
        replacement.SessionCursors.RemoveAll(x => x.SessionId == input.SessionId);
        replacement.SessionCursors.Add(new VoiceSessionCursor { SessionId = input.SessionId, ProcessedThroughUtc = input.EndsAtUtc });
        Recalculate(replacement);
        replacement.Revision = (rollOver || current == null) ? 1 : current.Revision + 1;
        replacement.ProjectionRevision++;
        replacement.UpdatedAtUtc = input.UpdatedAtUtc;
        if (input.AlreadyProjected && (current == null || current.ProjectionStatus == VoiceActivityProjectionStatus.Applied))
        {
            replacement.ProjectionStatus = VoiceActivityProjectionStatus.Applied;
            replacement.ProjectedRevision = replacement.ProjectionRevision;
            replacement.ProjectedEligibleSeconds = replacement.TotalEligibleSeconds;
            replacement.ProjectedXp = replacement.TotalAwardedXp;
            replacement.ProjectedSeasonTotals = ProjectedTotals(replacement.SeasonTotals);
            replacement.SeasonTotals = ProjectedTotals(replacement.SeasonTotals);
            replacement.ProjectedAtUtc = input.UpdatedAtUtc;
        }
        else if (input.AlreadyProjected)
        {
            replacement.ProjectedEligibleSeconds += segment.EligibleSeconds;
            replacement.ProjectedXp += segment.AwardedXp;
            var projectedSeason = replacement.ProjectedSeasonTotals.SingleOrDefault(x => x.SeasonId == segment.SeasonId);
            if (projectedSeason == null) replacement.ProjectedSeasonTotals.Add(new VoiceSeasonTotal { SeasonId = segment.SeasonId, EligibleSeconds = segment.EligibleSeconds, AwardedXp = segment.AwardedXp, ProjectedEligibleSeconds = segment.EligibleSeconds, ProjectedXp = segment.AwardedXp });
            else { projectedSeason.EligibleSeconds += segment.EligibleSeconds; projectedSeason.AwardedXp += segment.AwardedXp; projectedSeason.ProjectedEligibleSeconds += segment.EligibleSeconds; projectedSeason.ProjectedXp += segment.AwardedXp; }
        }
        else if (replacement.ProjectionStatus != VoiceActivityProjectionStatus.Projecting)
        {
            replacement.ProjectionStatus = VoiceActivityProjectionStatus.Pending;
        }
        return new(false, partial, gap, rollOver, segment, replacement);
    }

    public static IReadOnlyList<VoiceActivityInterval> Split(DateTime start, DateTime end, IEnumerable<DateTime> boundaries)
    {
        start = Utc(start); end = Utc(end);
        if (end <= start) return [];
        var points = boundaries.Select(Utc).Append(start).Append(end).Where(x => start <= x && x <= end).Distinct().Order().ToArray();
        return points.Zip(points.Skip(1), (from, to) => new VoiceActivityInterval(from, to)).ToArray();
    }

    public static List<VoiceActivitySegment> Merge(IEnumerable<VoiceActivitySegment> segments)
    {
        var merged = new List<VoiceActivitySegment>();
        foreach (var source in segments.OrderBy(x => x.StartsAtUtc).ThenBy(x => x.EndsAtUtc))
        {
            var segment = Copy(source);
            if (merged.Count > 0 && CanMerge(merged[^1], segment))
            {
                merged[^1].EndsAtUtc = segment.EndsAtUtc;
                merged[^1].EligibleSeconds += segment.EligibleSeconds;
                merged[^1].AwardedXp += segment.AwardedXp;
            }
            else merged.Add(segment);
        }
        return merged;
    }

    private static VoiceActivityDay NewDay(VoiceAccrualSlice input, int part, VoiceActivitySegment segment) => new()
    {
        GuildId = input.GuildId, UserId = input.UserId, DayStartUtc = input.StartsAtUtc.Date, Part = part,
        Segments = [segment], ProjectionStatus = input.AlreadyProjected ? VoiceActivityProjectionStatus.Applied : VoiceActivityProjectionStatus.Pending,
        CreatedAtUtc = input.UpdatedAtUtc
    };

    private static VoiceActivityDay Copy(VoiceActivityDay day) => new()
    {
        Id = day.Id, GuildId = day.GuildId, UserId = day.UserId, DayStartUtc = day.DayStartUtc, Part = day.Part, Revision = day.Revision,
        Segments = day.Segments.Select(Copy).ToList(), SessionCursors = day.SessionCursors.Select(x => new VoiceSessionCursor { SessionId = x.SessionId, ProcessedThroughUtc = x.ProcessedThroughUtc }).ToList(),
        TotalEligibleSeconds = day.TotalEligibleSeconds, TotalAwardedXp = day.TotalAwardedXp, SeasonTotals = CopyTotals(day.SeasonTotals), ProjectionStatus = day.ProjectionStatus,
        ProjectionRevision = day.ProjectionRevision, ProjectedRevision = day.ProjectedRevision, ProjectedEligibleSeconds = day.ProjectedEligibleSeconds,
        ProjectionTargetRevision = day.ProjectionTargetRevision, ProjectionTargetEligibleSeconds = day.ProjectionTargetEligibleSeconds, ProjectionTargetAwardedXp = day.ProjectionTargetAwardedXp,
        ProjectionTargetSeasonTotals = CopyTotals(day.ProjectionTargetSeasonTotals), ProjectedXp = day.ProjectedXp, ProjectedSeasonTotals = CopyTotals(day.ProjectedSeasonTotals), ProjectionLeaseOwner = day.ProjectionLeaseOwner,
        ProjectionLeaseExpiresAtUtc = day.ProjectionLeaseExpiresAtUtc, ProjectedAtUtc = day.ProjectedAtUtc, CreatedAtUtc = day.CreatedAtUtc, UpdatedAtUtc = day.UpdatedAtUtc
    };

    private static VoiceActivitySegment ToSegment(VoiceAccrualSlice input, DateTime start)
    {
        var seconds = Math.Max(0, input.EligibleSeconds - (long)(start - input.StartsAtUtc).TotalSeconds);
        return new VoiceActivitySegment
        {
            SessionId = input.SessionId, StartsAtUtc = start, EndsAtUtc = input.EndsAtUtc, ChannelId = input.ChannelId, SeasonId = input.SeasonId,
            EligibleSeconds = seconds, AwardedXp = VoiceXpWatchdog.RoundAccrual(seconds, input.EffectiveXpPerMinute), EffectiveXpPerMinute = input.EffectiveXpPerMinute,
            ChannelMultiplier = input.ChannelMultiplier, AppliedServerBoosterMultiplier = input.ServerBoosterMultiplier, SettingsRevision = input.SettingsRevision
        };
    }

    private static VoiceActivitySegment Copy(VoiceActivitySegment x) => new()
    {
        SessionId = x.SessionId, StartsAtUtc = x.StartsAtUtc, EndsAtUtc = x.EndsAtUtc, ChannelId = x.ChannelId, SeasonId = x.SeasonId,
        EligibleSeconds = x.EligibleSeconds, AwardedXp = x.AwardedXp, EffectiveXpPerMinute = x.EffectiveXpPerMinute, ChannelMultiplier = x.ChannelMultiplier,
        AppliedServerBoosterMultiplier = x.AppliedServerBoosterMultiplier, SettingsRevision = x.SettingsRevision
    };

    private static bool CanMerge(VoiceActivitySegment left, VoiceActivitySegment right) =>
        left.EndsAtUtc == right.StartsAtUtc && left.SessionId == right.SessionId && left.ChannelId == right.ChannelId && left.SeasonId == right.SeasonId &&
        left.EffectiveXpPerMinute == right.EffectiveXpPerMinute && left.ChannelMultiplier == right.ChannelMultiplier &&
        left.AppliedServerBoosterMultiplier == right.AppliedServerBoosterMultiplier && left.SettingsRevision == right.SettingsRevision;

    private static void Recalculate(VoiceActivityDay day)
    {
        day.TotalEligibleSeconds = day.Segments.Sum(x => x.EligibleSeconds);
        day.TotalAwardedXp = day.Segments.Sum(x => x.AwardedXp);
        day.SeasonTotals = day.Segments.GroupBy(x => x.SeasonId, StringComparer.Ordinal)
            .Select(x => (Group: x, Projected: day.ProjectedSeasonTotals.SingleOrDefault(y => y.SeasonId == x.Key)))
            .Select(x => new VoiceSeasonTotal { SeasonId = x.Group.Key, EligibleSeconds = x.Group.Sum(y => y.EligibleSeconds), AwardedXp = x.Group.Sum(y => y.AwardedXp), ProjectedEligibleSeconds = x.Projected?.EligibleSeconds ?? 0, ProjectedXp = x.Projected?.AwardedXp ?? 0 }).ToList();
    }

    private static List<VoiceSeasonTotal> CopyTotals(IEnumerable<VoiceSeasonTotal> values) => values.Select(x => new VoiceSeasonTotal { SeasonId = x.SeasonId, EligibleSeconds = x.EligibleSeconds, AwardedXp = x.AwardedXp, ProjectedEligibleSeconds = x.ProjectedEligibleSeconds, ProjectedXp = x.ProjectedXp }).ToList();
    private static List<VoiceSeasonTotal> ProjectedTotals(IEnumerable<VoiceSeasonTotal> values) => values.Select(x => new VoiceSeasonTotal { SeasonId = x.SeasonId, EligibleSeconds = x.EligibleSeconds, AwardedXp = x.AwardedXp, ProjectedEligibleSeconds = x.EligibleSeconds, ProjectedXp = x.AwardedXp }).ToList();

    private static VoiceAccrualSlice Normalize(VoiceAccrualSlice input) => input with { StartsAtUtc = Utc(input.StartsAtUtc), EndsAtUtc = Utc(input.EndsAtUtc), UpdatedAtUtc = Utc(input.UpdatedAtUtc) };
    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static void Validate(VoiceAccrualSlice input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.SessionId);
        if (input.EndsAtUtc <= input.StartsAtUtc) throw new ArgumentOutOfRangeException(nameof(input), "Voice accrual must have positive duration.");
        if (input.StartsAtUtc.Date != input.EndsAtUtc.AddTicks(-1).Date) throw new ArgumentException("Voice accrual must not cross a UTC day boundary.", nameof(input));
        if (input.EligibleSeconds < 0 || input.AwardedXp < 0) throw new ArgumentOutOfRangeException(nameof(input), "Voice accrual totals cannot be negative.");
    }
}
