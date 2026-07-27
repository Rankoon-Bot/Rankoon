using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;

namespace Rankoon.Data.Xp;

public sealed record XpAuditMemberItem(ulong UserId, string DisplayName, bool IsCurrentMember, decimal TotalXp, int Level, string? IconUrl);
public sealed record XpAuditMemberPage(IReadOnlyList<XpAuditMemberItem> Items, string? NextCursor);
public sealed record XpAuditTotals(decimal ImportedXp, decimal EarnedXp, decimal ManualAdjustment, decimal TotalXp, int Level, long Rank);
public sealed record XpAuditSeasonTotals(string SeasonId, string Name, decimal StartingXp, decimal EarnedXp, decimal ManualAdjustment, decimal TotalXp, int Level, long Rank);
public sealed record XpAuditPermissions(bool CanAdjust, bool IsSelf, bool IsOwner);
public sealed record XpAuditMemberDetails(ulong UserId, string DisplayName, bool IsCurrentMember, string? IconUrl, DateTime? LastXpActivityAtUtc, XpAuditTotals Lifetime, XpAuditSeasonTotals? ActiveSeason, XpAuditPermissions Permissions);
public sealed record XpAuditEntryItem(string Id, string GrantKey, string Source, XpLedgerEntryKind Kind, XpLedgerScope Scope, decimal Amount, string DisplayName, DateTime OccurredAtUtc, DateTime CreatedAtUtc, DateTime? ProjectedAtUtc, SeasonProjectionStatus ProjectionStatus, ulong? ChannelId, string? SeasonId, string? SeasonName, DateTime? PeriodStartsAtUtc, DateTime? PeriodEndsAtUtc, ulong? ActorUserId, string? ActorDisplayName, string? Reason, string? Reference, string? RequestId, string? ReversesGrantKey, string? ReversesLedgerEntryId, string? ReversedByLedgerEntryId);
public sealed record XpAuditEntryPage(IReadOnlyList<XpAuditEntryItem> Items, string? NextCursor);
public enum XpAuditTimelineItemType { Entry, VoiceDay }
public sealed record XpAuditVoiceDayItem(string Day, decimal TotalXp, long EligibleSeconds, int SegmentCount, int SessionCount, IReadOnlyList<string> SessionIds, int ChannelCount, IReadOnlyList<ulong> ChannelIds, IReadOnlyList<string> SeasonIds, DateTime ActivityStartsAtUtc, DateTime ActivityEndsAtUtc);
public sealed record XpAuditTimelineItem(XpAuditTimelineItemType ItemType, string Id, XpAuditEntryItem? Entry, XpAuditVoiceDayItem? VoiceDay, string Source, XpLedgerEntryKind Kind, XpLedgerScope Scope, decimal TotalAmount, int EntryCount, DateTime OccurredFromUtc, DateTime OccurredToUtc, DateTime? PeriodStartsAtUtc, DateTime? PeriodEndsAtUtc, long? DurationSeconds, ulong? ChannelId, string? SeasonId, string? SeasonName, SeasonProjectionStatus ProjectionStatus, decimal? AppliedServerBoosterMultiplier, bool IsPartial, string? DayKey = null);
public sealed record XpAuditTimelinePage(IReadOnlyList<XpAuditTimelineItem> Items, string? NextCursor);
public sealed record XpAuditVoiceSegmentItem(string Id, DateTime StartsAtUtc, DateTime EndsAtUtc, long DurationSeconds, decimal Xp, ulong ChannelId, string? SeasonId, string? SeasonName, decimal EffectiveXpPerMinute, decimal ChannelMultiplier, decimal? ServerBoosterMultiplier);
public sealed record XpAuditVoiceSegmentPage(IReadOnlyList<XpAuditVoiceSegmentItem> Items);
public sealed record ManualXpAdjustmentRequest(decimal Amount, XpLedgerScope Scope, string Reason, string? Reference, Guid RequestId);
public sealed record ManualXpAdjustmentResult(XpLedgerEntry Entry, bool AffectedActiveSeason, bool Existing);

public interface IXpAuditService
{
    Task<XpAuditMemberPage> SearchMembersAsync(ulong guildId, string? query, bool includeFormerMembers, int take, string? cursor, CancellationToken cancellationToken = default);
    Task<XpAuditMemberDetails?> GetMemberDetailsAsync(ulong guildId, ulong userId, bool canAdjust, bool isSelf, bool isOwner, CancellationToken cancellationToken = default);
    Task<XpAuditEntryPage> GetEntriesAsync(ulong guildId, ulong userId, XpAuditEntryFilter filter, CancellationToken cancellationToken = default);
    Task<XpAuditTimelinePage> GetTimelineAsync(ulong guildId, ulong userId, XpAuditEntryFilter filter, CancellationToken cancellationToken = default);
    Task<XpAuditVoiceSegmentPage> GetVoiceDaySegmentsAsync(ulong guildId, ulong userId, string dayKey, XpAuditEntryFilter filter, CancellationToken cancellationToken = default);
    Task<ManualXpAdjustmentResult> CreateAdjustmentAsync(ulong guildId, ulong userId, ulong actorId, string actorDisplayName, ManualXpAdjustmentRequest request, CancellationToken cancellationToken = default);
    Task<ManualXpAdjustmentResult> ReverseAdjustmentAsync(ulong guildId, string entryId, ulong actorId, string actorDisplayName, string reason, string? reference, Guid requestId, CancellationToken cancellationToken = default);
}

public sealed record XpAuditEntryFilter(string? Source, XpLedgerEntryKind? Kind, XpLedgerScope? Scope, string? SeasonId, ulong? ActorUserId, string? Direction, SeasonProjectionStatus? ProjectionStatus, DateTime? From, DateTime? To, ulong? ChannelId = null, int Take = 50, string? Cursor = null);

public sealed class XpAuditConflictException(string code) : Exception(code) { public string Code { get; } = code; }
public sealed class XpAuditValidationException(string code) : Exception(code) { public string Code { get; } = code; }

public sealed class XpAuditService(RankoonDbContext database, XpService xp, ISeasonService seasons, IReportWriter reports, TimeProvider timeProvider, IConfiguration configuration, IGuildUserPresentationService presentations) : IXpAuditService
{
    private readonly byte[] cursorKey = Encoding.UTF8.GetBytes(configuration["Jwt:SecretKey"] ?? "rankoon-xp-audit-cursor");

    public async Task<XpAuditMemberPage> SearchMembersAsync(ulong guildId, string? query, bool includeFormerMembers, int take, string? cursor, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 100); var normalized = (query ?? string.Empty).Trim().ToLowerInvariant();
        var fingerprint = $"{normalized}|{includeFormerMembers}"; var after = ReadCursor(cursor, guildId, 0, fingerprint);
        var filter = Builders<MemberXp>.Filter.Eq(x => x.GuildId, guildId);
        if (!includeFormerMembers) filter &= Builders<MemberXp>.Filter.Eq(x => x.IsCurrentMember, true);
        if (ulong.TryParse(normalized, out var userId)) filter &= Builders<MemberXp>.Filter.Eq(x => x.UserId, userId);
        else if (normalized.Length > 0) filter &= new BsonDocument("normalized_display_name", new BsonRegularExpression("^" + RegexEscape(normalized)));
        if (after != null) filter &= Builders<MemberXp>.Filter.Or(Builders<MemberXp>.Filter.Gt(x => x.NormalizedDisplayName, after.Name!), Builders<MemberXp>.Filter.And(Builders<MemberXp>.Filter.Eq(x => x.NormalizedDisplayName, after.Name), Builders<MemberXp>.Filter.Gt(x => x.UserId, after.UserId)));
        var rows = await database.MemberXp.Find(filter).SortBy(x => x.NormalizedDisplayName).ThenBy(x => x.UserId).Limit(take + 1).ToListAsync(ct);
        var more = rows.Count > take; var pageRows = rows.Take(take).ToArray();
        IReadOnlyDictionary<ulong, string?> icons;
        try { icons = await presentations.ResolveIconUrlsAsync(guildId, pageRows.Select(x => x.UserId), ct); } catch { icons = pageRows.ToDictionary(x => x.UserId, _ => (string?)null); }
        var items = pageRows.Select(x => new XpAuditMemberItem(x.UserId, x.DisplayName, x.IsCurrentMember, x.TotalXp, Mee6LevelCurve.GetLevel(x.TotalXp), icons.GetValueOrDefault(x.UserId))).ToArray();
        return new(items, more ? WriteCursor(guildId, 0, fingerprint, rows[take - 1].NormalizedDisplayName, rows[take - 1].UserId, null, null) : null);
    }

    public async Task<XpAuditMemberDetails?> GetMemberDetailsAsync(ulong guildId, ulong userId, bool canAdjust, bool isSelf, bool isOwner, CancellationToken ct = default)
    {
        var member = await database.MemberXp.Find(x => x.GuildId == guildId && x.UserId == userId).FirstOrDefaultAsync(ct); if (member == null) return null;
        var latestTask = database.XpLedger.Find(x => x.GuildId == guildId && x.UserId == userId && !x.IsProjectionControl && x.Source != "voice").SortByDescending(x => x.OccurredAtUtc).FirstOrDefaultAsync(ct);
        var latestVoiceTask = GetLatestVoiceActivityAsync(guildId, userId, ct);
        await Task.WhenAll(latestTask, latestVoiceTask);
        var latest = await latestTask; var latestVoice = await latestVoiceTask;
        var rank = await RankAsync(database.MemberXp, x => x.GuildId == guildId, member.TotalXp, userId, ct);
        var lifetime = new XpAuditTotals(member.ImportedMee6Xp, member.EarnedXp, member.ManualAdjustment, member.TotalXp, Mee6LevelCurve.GetLevel(member.TotalXp), rank);
        var active = await seasons.ResolveAsync(guildId, timeProvider.GetUtcNow().UtcDateTime, ct);
        XpAuditSeasonTotals? season = null;
        if (active?.Id != null)
        {
            var value = await database.SeasonMemberXp.Find(x => x.SeasonId == active.Id && x.UserId == userId).FirstOrDefaultAsync(ct);
            if (value != null) season = new(active.Id, active.Name, value.StartingXp, value.EarnedXp, value.ManualAdjustment, value.TotalXp, Mee6LevelCurve.GetLevel(value.TotalXp), await RankAsync(database.SeasonMemberXp, x => x.SeasonId == active.Id, value.TotalXp, userId, ct));
        }
        string? icon = null; try { icon = (await presentations.ResolveIconUrlsAsync(guildId, [userId], ct)).GetValueOrDefault(userId); } catch { }
        var lastActivity = latest?.OccurredAtUtc;
        if (latestVoice != null && (lastActivity == null || latestVoice > lastActivity)) lastActivity = latestVoice;
        return new(member.UserId, member.DisplayName, member.IsCurrentMember, icon, lastActivity, lifetime, season, new(canAdjust, isSelf, isOwner));
    }

    public async Task<XpAuditEntryPage> GetEntriesAsync(ulong guildId, ulong userId, XpAuditEntryFilter input, CancellationToken ct = default)
    {
        if (RequiresVoiceTimeline(input.Source)) throw new XpAuditValidationException("xpAudit.voiceRequiresTimeline");
        var take = Math.Clamp(input.Take, 1, 100); var fp = JsonSerializer.Serialize(input with { Take = 0, Cursor = null }); var after = ReadCursor(input.Cursor, guildId, userId, fp);
        var authority = HistoryAuthority(await GetVoiceMigrationStateAsync(ct));
        var filter = Builders<XpLedgerEntry>.Filter.Eq(x => x.GuildId, guildId) & Builders<XpLedgerEntry>.Filter.Eq(x => x.UserId, userId) & Builders<XpLedgerEntry>.Filter.Ne(x => x.IsProjectionControl, true);
        if (!authority.IncludeLegacyLedger) filter &= Builders<XpLedgerEntry>.Filter.Ne(x => x.Source, "voice");
        if (!string.IsNullOrWhiteSpace(input.Source)) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.Source, input.Source);
        if (input.Kind != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.Kind, input.Kind);
        if (input.Scope != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.Scope, input.Scope);
        if (input.SeasonId != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.SeasonId, input.SeasonId);
        if (input.ActorUserId != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.ActorUserId, input.ActorUserId);
        if (input.ProjectionStatus != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.ProjectionStatus, input.ProjectionStatus);
        if (input.ChannelId != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.ChannelId, input.ChannelId);
        if (input.From != null) filter &= Builders<XpLedgerEntry>.Filter.Gte(x => x.OccurredAtUtc, input.From.Value);
        if (input.To != null) filter &= Builders<XpLedgerEntry>.Filter.Lte(x => x.OccurredAtUtc, input.To.Value);
        if (input.Direction == "Positive") filter &= Builders<XpLedgerEntry>.Filter.Gt(x => x.Amount, 0); if (input.Direction == "Negative") filter &= Builders<XpLedgerEntry>.Filter.Lt(x => x.Amount, 0);
        if (after != null && ObjectId.TryParse(after.Id, out var oid)) filter &= new BsonDocument("$or", new BsonArray { new BsonDocument("occurred_at_utc", new BsonDocument("$lt", after.OccurredAt)), new BsonDocument { { "occurred_at_utc", after.OccurredAt }, { "_id", new BsonDocument("$lt", oid) } } });
        var rows = await database.XpLedger.Find(filter).SortByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id).Limit(take + 1).ToListAsync(ct); var ids = rows.Take(take).Select(x => x.Id).Where(x => x != null).ToArray();
        var reversals = await database.XpLedger.Find(x => x.ReversesLedgerEntryId != null && ids.Contains(x.ReversesLedgerEntryId)).ToListAsync(ct); var reversed = reversals.ToDictionary(x => x.ReversesLedgerEntryId!, x => x.Id);
        var seasonNames = (await database.GuildSeasons.Find(x => x.GuildId == guildId).ToListAsync(ct)).ToDictionary(x => x.Id!, x => x.Name);
        var items = rows.Take(take).Select(x => new XpAuditEntryItem(x.Id!, x.GrantKey, x.Source, XpLedgerSemantics.GetEffectiveKind(x), XpLedgerSemantics.GetEffectiveScope(x), x.Amount, x.DisplayName, x.OccurredAtUtc, x.CreatedAt, x.ProjectedAtUtc, x.ProjectionStatus, x.ChannelId, x.SeasonId, x.SeasonId != null ? seasonNames.GetValueOrDefault(x.SeasonId) : null, x.PeriodStartsAtUtc, x.PeriodEndsAtUtc, x.ActorUserId, x.ActorDisplayName, x.Reason, x.Reference, x.RequestId, x.ReversesGrantKey, x.ReversesLedgerEntryId, x.Id != null ? reversed.GetValueOrDefault(x.Id) : null)).ToArray();
        return new(items, rows.Count > take ? WriteCursor(guildId, userId, fp, null, 0, rows[take - 1].OccurredAtUtc, rows[take - 1].Id) : null);
    }

    public async Task<XpAuditTimelinePage> GetTimelineAsync(ulong guildId, ulong userId, XpAuditEntryFilter input, CancellationToken ct = default)
    {
        var take = Math.Clamp(input.Take, 1, 100); var fp = JsonSerializer.Serialize(input with { Take = 0, Cursor = null }); var after = ReadCursor(input.Cursor, guildId, userId, fp);
        var seasonNames = (await database.GuildSeasons.Find(x => x.GuildId == guildId).ToListAsync(ct)).ToDictionary(x => x.Id!, x => x.Name);
        var candidates = new List<XpAuditTimelineItem>();
        var authority = HistoryAuthority(await GetVoiceMigrationStateAsync(ct));

        if (!string.Equals(input.Source, "voice", StringComparison.OrdinalIgnoreCase) || authority.IncludeLegacyLedger)
        {
            var rows = await database.XpLedger.Find(BuildEntryFilter(guildId, userId, input, after, excludeVoice: !authority.IncludeLegacyLedger))
                .SortByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id).Limit(take + 1).ToListAsync(ct);
            var entries = await MapEntriesAsync(rows, seasonNames, ct);
            candidates.AddRange(entries.Select(ToTimelineEntry));
        }

        if (VoiceMatchesFilter(input))
        {
            var segments = (await ReadVoiceSegmentsAsync(guildId, userId, input, take + 2, after, authority.IncludeMigratedCompressedSegments, ct)).Where(x => MatchesVoiceFilter(x, input));
            candidates.AddRange(BuildVoiceDays(segments, seasonNames));
        }

        var ordered = candidates
            .Where(x => IsAfterCursor(x.OccurredToUtc, x.Id, after))
            .OrderByDescending(x => x.OccurredToUtc).ThenByDescending(x => x.Id, StringComparer.Ordinal)
            .Take(take + 1).ToArray();
        var returned = ordered.Take(take).ToArray();
        if (returned.Length == 0) return new(returned, null);
        var last = returned[^1];
        return new(returned, ordered.Length > take ? WriteCursor(guildId, userId, fp, null, 0, last.OccurredToUtc, last.Id) : null);
    }

    public async Task<XpAuditVoiceSegmentPage> GetVoiceDaySegmentsAsync(ulong guildId, ulong userId, string dayKey, XpAuditEntryFilter input, CancellationToken ct = default)
    {
        if (!DateOnly.TryParseExact(dayKey, "yyyy-MM-dd", out _)) throw new XpAuditValidationException("xpAudit.invalidVoiceDay");
        if (!VoiceMatchesFilter(input)) return new([]);
        var seasonNames = (await database.GuildSeasons.Find(x => x.GuildId == guildId).ToListAsync(ct)).ToDictionary(x => x.Id!, x => x.Name);
        var authority = HistoryAuthority(await GetVoiceMigrationStateAsync(ct));
        var day = DateOnly.ParseExact(dayKey, "yyyy-MM-dd").ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var items = (await ReadVoiceDayPartsAsync(guildId, userId, [day], ct))
            .SelectMany(x => ToVoiceSegments(x, authority.IncludeMigratedCompressedSegments)).Where(x => MatchesVoiceFilter(x, input))
            .OrderByDescending(x => x.EndsAtUtc).ThenByDescending(x => x.Id, StringComparer.Ordinal)
            .Select(x => new XpAuditVoiceSegmentItem(x.Id, x.StartsAtUtc, x.EndsAtUtc, x.EligibleSeconds, x.AwardedXp, x.ChannelId, x.SeasonId, x.SeasonId == null ? null : seasonNames.GetValueOrDefault(x.SeasonId), x.EffectiveXpPerMinute, x.ChannelMultiplier, x.ServerBoosterMultiplier))
            .ToArray();
        return new(items);
    }

    private FilterDefinition<XpLedgerEntry> BuildEntryFilter(ulong guildId, ulong userId, XpAuditEntryFilter input, Cursor? after, bool excludeVoice = false)
    {
        var filter = Builders<XpLedgerEntry>.Filter.Eq(x => x.GuildId, guildId) & Builders<XpLedgerEntry>.Filter.Eq(x => x.UserId, userId) & Builders<XpLedgerEntry>.Filter.Ne(x => x.IsProjectionControl, true);
        if (excludeVoice) filter &= Builders<XpLedgerEntry>.Filter.Ne(x => x.Source, "voice");
        if (!string.IsNullOrWhiteSpace(input.Source)) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.Source, input.Source);
        if (input.Kind != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.Kind, input.Kind); if (input.Scope != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.Scope, input.Scope);
        if (input.SeasonId != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.SeasonId, input.SeasonId); if (input.ActorUserId != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.ActorUserId, input.ActorUserId);
        if (input.ProjectionStatus != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.ProjectionStatus, input.ProjectionStatus); if (input.ChannelId != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.ChannelId, input.ChannelId);
        if (input.From != null) filter &= Builders<XpLedgerEntry>.Filter.Gte(x => x.OccurredAtUtc, input.From.Value); if (input.To != null) filter &= Builders<XpLedgerEntry>.Filter.Lte(x => x.OccurredAtUtc, input.To.Value);
        if (input.Direction == "Positive") filter &= Builders<XpLedgerEntry>.Filter.Gt(x => x.Amount, 0); if (input.Direction == "Negative") filter &= Builders<XpLedgerEntry>.Filter.Lt(x => x.Amount, 0);
        if (after?.OccurredAt != null)
        {
            if (ObjectId.TryParse(after.Id, out var oid)) filter &= new BsonDocument("$or", new BsonArray { new BsonDocument("occurred_at_utc", new BsonDocument("$lt", after.OccurredAt)), new BsonDocument { { "occurred_at_utc", after.OccurredAt }, { "_id", new BsonDocument("$lt", oid) } } });
            else filter &= Builders<XpLedgerEntry>.Filter.Lte(x => x.OccurredAtUtc, after.OccurredAt.Value);
        }
        return filter;
    }

    private async Task<XpAuditEntryItem[]> MapEntriesAsync(IReadOnlyCollection<XpLedgerEntry> rows, IReadOnlyDictionary<string, string> seasonNames, CancellationToken ct)
    {
        var ids = rows.Select(x => x.Id).Where(x => x != null).ToArray(); var reversals = await database.XpLedger.Find(x => x.ReversesLedgerEntryId != null && ids.Contains(x.ReversesLedgerEntryId)).ToListAsync(ct); var reversed = reversals.ToDictionary(x => x.ReversesLedgerEntryId!, x => x.Id);
        return rows.Select(x => new XpAuditEntryItem(x.Id!, x.GrantKey, x.Source, XpLedgerSemantics.GetEffectiveKind(x), XpLedgerSemantics.GetEffectiveScope(x), x.Amount, x.DisplayName, x.OccurredAtUtc, x.CreatedAt, x.ProjectedAtUtc, x.ProjectionStatus, x.ChannelId, x.SeasonId, x.SeasonId != null ? seasonNames.GetValueOrDefault(x.SeasonId) : null, x.PeriodStartsAtUtc, x.PeriodEndsAtUtc, x.ActorUserId, x.ActorDisplayName, x.Reason, x.Reference, x.RequestId, x.ReversesGrantKey, x.ReversesLedgerEntryId, x.Id != null ? reversed.GetValueOrDefault(x.Id) : null)).ToArray();
    }

    private static XpAuditTimelineItem ToTimelineEntry(XpAuditEntryItem entry) => new(XpAuditTimelineItemType.Entry, entry.Id, entry, null, entry.Source, entry.Kind, entry.Scope, entry.Amount, 1, entry.OccurredAtUtc, entry.OccurredAtUtc, entry.PeriodStartsAtUtc, entry.PeriodEndsAtUtc, null, entry.ChannelId, entry.SeasonId, entry.SeasonName, entry.ProjectionStatus, null, false);

    internal static IEnumerable<XpAuditTimelineItem> BuildVoiceDays(IEnumerable<VoiceSegment> segments, IReadOnlyDictionary<string, string> seasonNames)
    {
        foreach (var day in segments.GroupBy(x => x.DayKey, StringComparer.Ordinal))
        {
            var values = day.ToArray();
            var channels = values.Select(x => x.ChannelId).Distinct().Order().ToArray();
            var seasonIds = values.Select(x => x.SeasonId).Where(x => x != null).Cast<string>().Distinct().Order(StringComparer.Ordinal).ToArray();
            var scope = values.Select(x => x.Scope).Distinct().Take(2).ToArray();
            var status = values.Select(x => x.ProjectionStatus).Distinct().Take(2).ToArray();
            var from = values.Min(x => x.StartsAtUtc); var to = values.Max(x => x.EndsAtUtc);
            var seasonId = seasonIds.Length == 1 ? seasonIds[0] : null;
            var sessions = values.Select(x => x.SessionId).Distinct().Order(StringComparer.Ordinal).ToArray();
            var voiceDay = new XpAuditVoiceDayItem(day.Key, values.Sum(x => x.AwardedXp), values.Sum(x => x.EligibleSeconds), values.Length, sessions.Length, sessions, channels.Length, channels, seasonIds, from, to);
            yield return new XpAuditTimelineItem(XpAuditTimelineItemType.VoiceDay, $"voice-day:{day.Key}", null, voiceDay, "voice", XpLedgerEntryKind.AutomaticGrant,
                scope.Length == 1 ? scope[0] : XpLedgerScope.LifetimeAndSeason, values.Sum(x => x.Amount), values.Length,
                from, to, from, to, values.Sum(x => x.EligibleSeconds), channels.Length == 1 ? channels[0] : null,
                seasonId, seasonId == null ? null : seasonNames.GetValueOrDefault(seasonId), status.Length == 1 ? status[0] : SeasonProjectionStatus.Applied,
                null, false, day.Key);
        }
    }

    private static bool VoiceMatchesFilter(XpAuditEntryFilter input) =>
        (string.IsNullOrWhiteSpace(input.Source) || string.Equals(input.Source, "voice", StringComparison.OrdinalIgnoreCase)) &&
        (input.Kind == null || input.Kind == XpLedgerEntryKind.AutomaticGrant) && input.ActorUserId == null && input.Direction != "Negative";

    internal static bool MatchesVoiceFilter(VoiceSegment segment, XpAuditEntryFilter input)
    {
        if (input.Scope != null && segment.Scope != input.Scope || input.SeasonId != null && segment.SeasonId != input.SeasonId || input.ChannelId != null && segment.ChannelId != input.ChannelId || input.ProjectionStatus != null && segment.ProjectionStatus != input.ProjectionStatus) return false;
        if (input.From != null && segment.StartsAtUtc < input.From || input.To != null && segment.StartsAtUtc > input.To) return false;
        return input.Direction != "Positive" || segment.AwardedXp > 0;
    }

    private static bool IsAfterCursor(DateTime occurred, string id, Cursor? after) => after?.OccurredAt == null || occurred < after.OccurredAt || occurred == after.OccurredAt && string.CompareOrdinal(id, after.Id) < 0;

    private async Task<IReadOnlyList<VoiceSegment>> ReadVoiceSegmentsAsync(ulong guildId, ulong userId, XpAuditEntryFilter input, int dayLimit, Cursor? after, bool includeMigratedCompressedSegments, CancellationToken ct)
    {
        var dayFilter = BuildVoiceDayFilter(guildId, userId, input);
        if (after?.OccurredAt != null)
        {
            var cursorDay = DateTime.SpecifyKind(after.OccurredAt.Value.Date, DateTimeKind.Utc);
            dayFilter &= Builders<VoiceActivityDay>.Filter.Lte(x => x.DayStartUtc, cursorDay);
        }
        var keys = await database.VoiceActivities.Aggregate()
            .Match(dayFilter)
            .Group(new BsonDocument("_id", "$day_start_utc"))
            .Sort(new BsonDocument("_id", -1))
            .Limit(dayLimit)
            .ToListAsync(ct);
        var days = keys.Select(x => x["_id"].ToUniversalTime()).ToArray();
        if (days.Length == 0) return [];
        return (await ReadVoiceDayPartsAsync(guildId, userId, days, ct)).SelectMany(x => ToVoiceSegments(x, includeMigratedCompressedSegments)).ToArray();
    }

    private FilterDefinition<VoiceActivityDay> BuildVoiceDayFilter(ulong guildId, ulong userId, XpAuditEntryFilter input)
    {
        var filter = Builders<VoiceActivityDay>.Filter.Eq(x => x.GuildId, guildId) & Builders<VoiceActivityDay>.Filter.Eq(x => x.UserId, userId);
        var segment = Builders<VoiceActivitySegment>.Filter.Empty;
        if (input.Scope == XpLedgerScope.LifetimeOnly) segment &= Builders<VoiceActivitySegment>.Filter.Eq(x => x.SeasonId, null);
        if (input.Scope is XpLedgerScope.LifetimeAndSeason or XpLedgerScope.SeasonOnly) segment &= Builders<VoiceActivitySegment>.Filter.Ne(x => x.SeasonId, null);
        if (input.SeasonId != null) segment &= Builders<VoiceActivitySegment>.Filter.Eq(x => x.SeasonId, input.SeasonId);
        if (input.ChannelId != null) segment &= Builders<VoiceActivitySegment>.Filter.Eq(x => x.ChannelId, input.ChannelId.Value);
        if (input.From != null) segment &= Builders<VoiceActivitySegment>.Filter.Gte(x => x.StartsAtUtc, input.From.Value);
        if (input.To != null) segment &= Builders<VoiceActivitySegment>.Filter.Lte(x => x.StartsAtUtc, input.To.Value);
        if (input.Direction == "Positive") segment &= Builders<VoiceActivitySegment>.Filter.Gt(x => x.AwardedXp, 0);
        filter &= Builders<VoiceActivityDay>.Filter.ElemMatch(x => x.Segments, segment);
        if (input.ProjectionStatus != null)
        {
            var voiceStatus = input.ProjectionStatus == SeasonProjectionStatus.Applied ? VoiceActivityProjectionStatus.Applied : VoiceActivityProjectionStatus.Pending;
            filter &= Builders<VoiceActivityDay>.Filter.Eq(x => x.ProjectionStatus, voiceStatus);
        }
        return filter;
    }

    private Task<List<VoiceActivityDay>> ReadVoiceDayPartsAsync(ulong guildId, ulong userId, IReadOnlyCollection<DateTime> days, CancellationToken ct) =>
        database.VoiceActivities.Find(Builders<VoiceActivityDay>.Filter.Eq(x => x.GuildId, guildId) & Builders<VoiceActivityDay>.Filter.Eq(x => x.UserId, userId) & Builders<VoiceActivityDay>.Filter.In(x => x.DayStartUtc, days)).ToListAsync(ct);

    private static IEnumerable<VoiceSegment> ToVoiceSegments(VoiceActivityDay day, bool includeMigratedCompressedSegments = true) => day.Segments.Select((segment, index) => (segment, index))
        .Where(x => includeMigratedCompressedSegments || x.segment.SettingsRevision != VoiceLedgerMigrationService.LegacySettingsRevision)
        .Select(x => new VoiceSegment(
        VoiceSegmentId(day, x.segment, x.index), day.DayStartUtc.ToString("yyyy-MM-dd"), x.segment.SessionId,
        x.segment.StartsAtUtc, x.segment.EndsAtUtc, x.segment.EligibleSeconds, x.segment.AwardedXp, x.segment.ChannelId, x.segment.SeasonId,
        x.segment.SeasonId == null ? XpLedgerScope.LifetimeOnly : XpLedgerScope.LifetimeAndSeason,
        day.ProjectionStatus == VoiceActivityProjectionStatus.Applied ? SeasonProjectionStatus.Applied : SeasonProjectionStatus.Pending,
        x.segment.EffectiveXpPerMinute, x.segment.ChannelMultiplier, x.segment.AppliedServerBoosterMultiplier));

    private static string VoiceSegmentId(VoiceActivityDay day, VoiceActivitySegment segment, int index)
    {
        var identity = $"{day.GuildId}:{day.UserId}:{day.DayStartUtc.Ticks}:{day.Part}:{index}:{segment.SessionId}:{segment.StartsAtUtc.Ticks}";
        return $"voice-segment:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))}";
    }

    private async Task<DateTime?> GetLatestVoiceActivityAsync(ulong guildId, ulong userId, CancellationToken ct)
    {
        var filter = Builders<VoiceActivityDay>.Filter.Eq(x => x.GuildId, guildId) & Builders<VoiceActivityDay>.Filter.Eq(x => x.UserId, userId) & Builders<VoiceActivityDay>.Filter.Exists("segments.0");
        var latestPart = await database.VoiceActivities.Find(filter).SortByDescending(x => x.DayStartUtc).FirstOrDefaultAsync(ct);
        if (latestPart == null) return null;
        return (await ReadVoiceDayPartsAsync(guildId, userId, [latestPart.DayStartUtc], ct)).SelectMany(x => x.Segments).Max(x => (DateTime?)x.EndsAtUtc);
    }

    internal static VoiceHistoryAuthority HistoryAuthority(VoiceLedgerMigrationState? state)
    {
        var compressed = VoiceLedgerMigrationService.IsCompressedVoiceAuthoritative(state);
        return new(!compressed, compressed);
    }

    private async Task<VoiceLedgerMigrationState?> GetVoiceMigrationStateAsync(CancellationToken ct) =>
        await database.VoiceLedgerMigrationStates.Find(x => x.Id == VoiceLedgerMigrationState.SingletonId).FirstOrDefaultAsync(ct);

    internal sealed record VoiceHistoryAuthority(bool IncludeLegacyLedger, bool IncludeMigratedCompressedSegments);

    internal static bool RequiresVoiceTimeline(string? source) => string.Equals(source, "voice", StringComparison.OrdinalIgnoreCase);

    internal sealed record VoiceSegment(string Id, string DayKey, string SessionId, DateTime StartsAtUtc, DateTime EndsAtUtc, long EligibleSeconds, decimal AwardedXp, ulong ChannelId, string? SeasonId, XpLedgerScope Scope, SeasonProjectionStatus ProjectionStatus, decimal EffectiveXpPerMinute, decimal ChannelMultiplier, decimal? ServerBoosterMultiplier)
    {
        public decimal Amount => AwardedXp;
    }

    public async Task<ManualXpAdjustmentResult> CreateAdjustmentAsync(ulong guildId, ulong userId, ulong actorId, string actorName, ManualXpAdjustmentRequest request, CancellationToken ct = default)
    {
        var existing = await database.XpLedger.Find(x => x.GrantKey == $"manual:{guildId}:{userId}:{request.RequestId:D}").FirstOrDefaultAsync(ct);
        if (existing != null) { if (existing.Amount != request.Amount || XpLedgerSemantics.GetEffectiveScope(existing) != request.Scope || existing.Reason != request.Reason || existing.Reference != request.Reference) throw new XpAuditConflictException("xpAdjustment.requestConflict"); if (existing.ProjectionStatus == SeasonProjectionStatus.Pending) await xp.ProjectAsync(existing, ct); return new(existing, existing.SeasonId != null, true); }
        var member = await database.MemberXp.Find(x => x.GuildId == guildId && x.UserId == userId).FirstOrDefaultAsync(ct) ?? throw new XpAuditValidationException("xpAudit.memberNotFound");
        var now = timeProvider.GetUtcNow().UtcDateTime; var season = request.Scope == XpLedgerScope.LifetimeAndSeason ? await seasons.ResolveAsync(guildId, now, ct) : null;
        var entry = new XpLedgerEntry { GrantKey = $"manual:{guildId}:{userId}:{request.RequestId:D}", GuildId = guildId, UserId = userId, DisplayName = member.DisplayName, Source = "manual_adjustment", Amount = request.Amount, Kind = XpLedgerEntryKind.ManualAdjustment, Scope = request.Scope, SeasonId = season?.Id, ActorUserId = actorId, ActorDisplayName = actorName, Reason = request.Reason, Reference = request.Reference, RequestId = request.RequestId.ToString("D"), OccurredAtUtc = now, CreatedAt = now };
        try { await database.XpLedger.InsertOneAsync(entry, cancellationToken: ct); } catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { return await CreateAdjustmentAsync(guildId, userId, actorId, actorName, request, ct); }
        await xp.ProjectAsync(entry, ct); await reports.WriteAsync(new(guildId, ReportCategories.Activity, ReportNames.XpManualAdjustmentCreated, ReportOutcomes.Succeeded, ActorId: actorId, SubjectId: userId, Metadata: new Dictionary<string, object?> { ["ledgerEntryId"] = entry.Id, ["amount"] = entry.Amount, ["scope"] = entry.Scope, ["seasonId"] = entry.SeasonId, ["reason"] = entry.Reason, ["reference"] = entry.Reference, ["requestId"] = entry.RequestId }), ct);
        return new(entry, season != null, false);
    }

    public async Task<ManualXpAdjustmentResult> ReverseAdjustmentAsync(ulong guildId, string entryId, ulong actorId, string actorName, string reason, string? reference, Guid requestId, CancellationToken ct = default)
    {
        var original = await database.XpLedger.Find(x => x.Id == entryId && x.GuildId == guildId).FirstOrDefaultAsync(ct) ?? throw new XpAuditValidationException("xpAdjustment.entryNotFound");
        if (XpLedgerSemantics.GetEffectiveKind(original) != XpLedgerEntryKind.ManualAdjustment) throw new XpAuditValidationException("xpAdjustment.notManual");
        var existing = await database.XpLedger.Find(x => x.GrantKey == $"manual-reversal:{entryId}:{requestId:D}").FirstOrDefaultAsync(ct); if (existing != null) return new(existing, existing.SeasonId != null, true);
        var now = timeProvider.GetUtcNow().UtcDateTime; var entry = new XpLedgerEntry { GrantKey = $"manual-reversal:{entryId}:{requestId:D}", GuildId = guildId, UserId = original.UserId, DisplayName = original.DisplayName, Source = "manual_adjustment_reversal", Amount = -original.Amount, Kind = XpLedgerEntryKind.ManualAdjustmentReversal, Scope = XpLedgerSemantics.GetEffectiveScope(original), SeasonId = original.SeasonId, ReversesGrantKey = original.GrantKey, ReversesLedgerEntryId = original.Id, ActorUserId = actorId, ActorDisplayName = actorName, Reason = reason, Reference = reference, RequestId = requestId.ToString("D"), OccurredAtUtc = now, CreatedAt = now };
        try { await database.XpLedger.InsertOneAsync(entry, cancellationToken: ct); } catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { throw new XpAuditConflictException("xpAdjustment.alreadyReversed"); }
        await xp.ProjectAsync(entry, ct); await reports.WriteAsync(new(guildId, ReportCategories.Activity, ReportNames.XpManualAdjustmentReversed, ReportOutcomes.Succeeded, ActorId: actorId, SubjectId: original.UserId, Metadata: new Dictionary<string, object?> { ["originalLedgerEntryId"] = original.Id, ["reversalLedgerEntryId"] = entry.Id, ["requestId"] = entry.RequestId }), ct); return new(entry, entry.SeasonId != null, false);
    }

    private static async Task<long> RankAsync<T>(IMongoCollection<T> collection, System.Linq.Expressions.Expression<Func<T, bool>> baseFilter, decimal total, ulong userId, CancellationToken ct) where T : class => 1 + await collection.CountDocumentsAsync(Builders<T>.Filter.And(Builders<T>.Filter.Where(baseFilter), new BsonDocument("$or", new BsonArray { new BsonDocument("total_xp", new BsonDocument("$gt", total)), new BsonDocument { { "total_xp", total }, { "user_id", new BsonDocument("$lt", new BsonInt64(unchecked((long)userId))) } } })), cancellationToken: ct);
    private string WriteCursor(ulong guild, ulong user, string filter, string? name, ulong id, DateTime? occurred, string? objectId) { var p = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Cursor(guild, user, filter, name, id, occurred, objectId)))); return p + "." + Convert.ToHexString(HMACSHA256.HashData(cursorKey, Encoding.UTF8.GetBytes(p))); }
    private Cursor? ReadCursor(string? value, ulong guild, ulong user, string filter)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            var parts = value.Split('.');
            if (parts.Length != 2 || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(parts[1]), HMACSHA256.HashData(cursorKey, Encoding.UTF8.GetBytes(parts[0])))) throw new XpAuditValidationException("xpAudit.invalidCursor");
            var cursor = JsonSerializer.Deserialize<Cursor>(Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])));
            if (cursor == null || cursor.Guild != guild || cursor.User != user || cursor.Filter != filter) throw new XpAuditValidationException("xpAudit.invalidCursor");
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or CryptographicException)
        {
            throw new XpAuditValidationException("xpAudit.invalidCursor");
        }
    }
    private static string RegexEscape(string value) => System.Text.RegularExpressions.Regex.Escape(value);
    private sealed record Cursor(ulong Guild, ulong User, string Filter, string? Name, ulong UserId, DateTime? OccurredAt, string? Id);
}
