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
public enum XpAuditTimelineItemType { Entry, Group }
public sealed record XpAuditTimelineItem(XpAuditTimelineItemType ItemType, string Id, XpAuditEntryItem? Entry, string Source, XpLedgerEntryKind Kind, XpLedgerScope Scope, decimal TotalAmount, int EntryCount, DateTime OccurredFromUtc, DateTime OccurredToUtc, DateTime? PeriodStartsAtUtc, DateTime? PeriodEndsAtUtc, long? DurationSeconds, ulong? ChannelId, string? SeasonId, string? SeasonName, SeasonProjectionStatus ProjectionStatus, decimal? AppliedServerBoosterMultiplier, bool IsPartial);
public sealed record XpAuditTimelinePage(IReadOnlyList<XpAuditTimelineItem> Items, string? NextCursor);
public sealed record ManualXpAdjustmentRequest(decimal Amount, XpLedgerScope Scope, string Reason, string? Reference, Guid RequestId);
public sealed record ManualXpAdjustmentResult(XpLedgerEntry Entry, bool AffectedActiveSeason, bool Existing);

public interface IXpAuditService
{
    Task<XpAuditMemberPage> SearchMembersAsync(ulong guildId, string? query, bool includeFormerMembers, int take, string? cursor, CancellationToken cancellationToken = default);
    Task<XpAuditMemberDetails?> GetMemberDetailsAsync(ulong guildId, ulong userId, bool canAdjust, bool isSelf, bool isOwner, CancellationToken cancellationToken = default);
    Task<XpAuditEntryPage> GetEntriesAsync(ulong guildId, ulong userId, XpAuditEntryFilter filter, CancellationToken cancellationToken = default);
    Task<XpAuditTimelinePage> GetTimelineAsync(ulong guildId, ulong userId, XpAuditEntryFilter filter, CancellationToken cancellationToken = default);
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
        try { icons = presentations.ResolveIconUrls(guildId, pageRows.Select(x => x.UserId)); } catch { icons = pageRows.ToDictionary(x => x.UserId, _ => (string?)null); }
        var items = pageRows.Select(x => new XpAuditMemberItem(x.UserId, x.DisplayName, x.IsCurrentMember, x.TotalXp, Mee6LevelCurve.GetLevel(x.TotalXp), icons.GetValueOrDefault(x.UserId))).ToArray();
        return new(items, more ? WriteCursor(guildId, 0, fingerprint, rows[take - 1].NormalizedDisplayName, rows[take - 1].UserId, null, null) : null);
    }

    public async Task<XpAuditMemberDetails?> GetMemberDetailsAsync(ulong guildId, ulong userId, bool canAdjust, bool isSelf, bool isOwner, CancellationToken ct = default)
    {
        var member = await database.MemberXp.Find(x => x.GuildId == guildId && x.UserId == userId).FirstOrDefaultAsync(ct); if (member == null) return null;
        var latest = await database.XpLedger.Find(x => x.GuildId == guildId && x.UserId == userId && !x.IsProjectionControl).SortByDescending(x => x.OccurredAtUtc).FirstOrDefaultAsync(ct);
        var rank = await RankAsync(database.MemberXp, x => x.GuildId == guildId, member.TotalXp, userId, ct);
        var lifetime = new XpAuditTotals(member.ImportedMee6Xp, member.EarnedXp, member.ManualAdjustment, member.TotalXp, Mee6LevelCurve.GetLevel(member.TotalXp), rank);
        var active = await seasons.ResolveAsync(guildId, timeProvider.GetUtcNow().UtcDateTime, ct);
        XpAuditSeasonTotals? season = null;
        if (active?.Id != null)
        {
            var value = await database.SeasonMemberXp.Find(x => x.SeasonId == active.Id && x.UserId == userId).FirstOrDefaultAsync(ct);
            if (value != null) season = new(active.Id, active.Name, value.StartingXp, value.EarnedXp, value.ManualAdjustment, value.TotalXp, Mee6LevelCurve.GetLevel(value.TotalXp), await RankAsync(database.SeasonMemberXp, x => x.SeasonId == active.Id, value.TotalXp, userId, ct));
        }
        string? icon = null; try { icon = presentations.ResolveIconUrls(guildId, [userId]).GetValueOrDefault(userId); } catch { }
        return new(member.UserId, member.DisplayName, member.IsCurrentMember, icon, latest?.OccurredAtUtc, lifetime, season, new(canAdjust, isSelf, isOwner));
    }

    public async Task<XpAuditEntryPage> GetEntriesAsync(ulong guildId, ulong userId, XpAuditEntryFilter input, CancellationToken ct = default)
    {
        var take = Math.Clamp(input.Take, 1, 100); var fp = JsonSerializer.Serialize(input with { Take = 0, Cursor = null }); var after = ReadCursor(input.Cursor, guildId, userId, fp);
        var filter = Builders<XpLedgerEntry>.Filter.Eq(x => x.GuildId, guildId) & Builders<XpLedgerEntry>.Filter.Eq(x => x.UserId, userId) & Builders<XpLedgerEntry>.Filter.Ne(x => x.IsProjectionControl, true);
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
        var filter = BuildEntryFilter(guildId, userId, input, after);
        // Bound raw scan. A partial final group tells client more same activity exists.
        var rows = await database.XpLedger.Find(filter).SortByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id).Limit(5001).ToListAsync(ct);
        var limited = rows.Count > 5000; var consumed = rows.Take(5000).ToArray();
        var seasonNames = (await database.GuildSeasons.Find(x => x.GuildId == guildId).ToListAsync(ct)).ToDictionary(x => x.Id!, x => x.Name);
        var mapped = await MapEntriesAsync(consumed, seasonNames, ct);
        var all = BuildTimeline(mapped, limited).ToArray();
        var returned = all.Take(take).ToArray();
        if (returned.Length == 0) return new(returned, null);
        var lastRawId = returned[^1].ItemType == XpAuditTimelineItemType.Entry ? returned[^1].Entry!.Id : returned[^1].Id.Split(':')[^1];
        var lastRaw = consumed.Last(x => x.Id == lastRawId);
        var more = all.Length > returned.Length || limited;
        return new(returned, more ? WriteCursor(guildId, userId, fp, null, 0, lastRaw.OccurredAtUtc, lastRaw.Id) : null);
    }

    private FilterDefinition<XpLedgerEntry> BuildEntryFilter(ulong guildId, ulong userId, XpAuditEntryFilter input, Cursor? after)
    {
        var filter = Builders<XpLedgerEntry>.Filter.Eq(x => x.GuildId, guildId) & Builders<XpLedgerEntry>.Filter.Eq(x => x.UserId, userId) & Builders<XpLedgerEntry>.Filter.Ne(x => x.IsProjectionControl, true);
        if (!string.IsNullOrWhiteSpace(input.Source)) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.Source, input.Source);
        if (input.Kind != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.Kind, input.Kind); if (input.Scope != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.Scope, input.Scope);
        if (input.SeasonId != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.SeasonId, input.SeasonId); if (input.ActorUserId != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.ActorUserId, input.ActorUserId);
        if (input.ProjectionStatus != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.ProjectionStatus, input.ProjectionStatus); if (input.ChannelId != null) filter &= Builders<XpLedgerEntry>.Filter.Eq(x => x.ChannelId, input.ChannelId);
        if (input.From != null) filter &= Builders<XpLedgerEntry>.Filter.Gte(x => x.OccurredAtUtc, input.From.Value); if (input.To != null) filter &= Builders<XpLedgerEntry>.Filter.Lte(x => x.OccurredAtUtc, input.To.Value);
        if (input.Direction == "Positive") filter &= Builders<XpLedgerEntry>.Filter.Gt(x => x.Amount, 0); if (input.Direction == "Negative") filter &= Builders<XpLedgerEntry>.Filter.Lt(x => x.Amount, 0);
        if (after != null && ObjectId.TryParse(after.Id, out var oid)) filter &= new BsonDocument("$or", new BsonArray { new BsonDocument("occurred_at_utc", new BsonDocument("$lt", after.OccurredAt)), new BsonDocument { { "occurred_at_utc", after.OccurredAt }, { "_id", new BsonDocument("$lt", oid) } } });
        return filter;
    }

    private async Task<XpAuditEntryItem[]> MapEntriesAsync(IReadOnlyCollection<XpLedgerEntry> rows, IReadOnlyDictionary<string, string> seasonNames, CancellationToken ct)
    {
        var ids = rows.Select(x => x.Id).Where(x => x != null).ToArray(); var reversals = await database.XpLedger.Find(x => x.ReversesLedgerEntryId != null && ids.Contains(x.ReversesLedgerEntryId)).ToListAsync(ct); var reversed = reversals.ToDictionary(x => x.ReversesLedgerEntryId!, x => x.Id);
        return rows.Select(x => new XpAuditEntryItem(x.Id!, x.GrantKey, x.Source, XpLedgerSemantics.GetEffectiveKind(x), XpLedgerSemantics.GetEffectiveScope(x), x.Amount, x.DisplayName, x.OccurredAtUtc, x.CreatedAt, x.ProjectedAtUtc, x.ProjectionStatus, x.ChannelId, x.SeasonId, x.SeasonId != null ? seasonNames.GetValueOrDefault(x.SeasonId) : null, x.PeriodStartsAtUtc, x.PeriodEndsAtUtc, x.ActorUserId, x.ActorDisplayName, x.Reason, x.Reference, x.RequestId, x.ReversesGrantKey, x.ReversesLedgerEntryId, x.Id != null ? reversed.GetValueOrDefault(x.Id) : null)).ToArray();
    }

    private static IEnumerable<XpAuditTimelineItem> BuildTimeline(IReadOnlyList<XpAuditEntryItem> rows, bool scanLimited)
    {
        for (var i = 0; i < rows.Count;)
        {
            var first = rows[i]; var group = new List<XpAuditEntryItem> { first }; i++;
            while (i < rows.Count && CanGroup(group[^1], rows[i])) { group.Add(rows[i]); i++; }
            if (group.Count == 1 || !IsVoiceGroupable(first)) { yield return new(XpAuditTimelineItemType.Entry, first.Id, first, first.Source, first.Kind, first.Scope, first.Amount, 1, first.OccurredAtUtc, first.OccurredAtUtc, first.PeriodStartsAtUtc, first.PeriodEndsAtUtc, null, first.ChannelId, first.SeasonId, first.SeasonName, first.ProjectionStatus, null, false); continue; }
            var oldest = group[^1]; var seconds = (long)(first.PeriodEndsAtUtc!.Value - oldest.PeriodStartsAtUtc!.Value).TotalSeconds;
            yield return new(XpAuditTimelineItemType.Group, $"voice:{first.Id}:{oldest.Id}", null, first.Source, first.Kind, first.Scope, group.Sum(x => x.Amount), group.Count, oldest.OccurredAtUtc, first.OccurredAtUtc, oldest.PeriodStartsAtUtc, first.PeriodEndsAtUtc, seconds, first.ChannelId, first.SeasonId, first.SeasonName, first.ProjectionStatus, null, scanLimited && i == rows.Count);
        }
    }
    private static bool IsVoiceGroupable(XpAuditEntryItem x) => x.Source == "voice" && x.Kind == XpLedgerEntryKind.AutomaticGrant && x.PeriodStartsAtUtc != null && x.PeriodEndsAtUtc != null && x.ChannelId != null && x.ActorUserId == null && string.IsNullOrEmpty(x.Reason) && string.IsNullOrEmpty(x.Reference) && x.ReversesLedgerEntryId == null && x.ReversedByLedgerEntryId == null;
    private static bool CanGroup(XpAuditEntryItem newer, XpAuditEntryItem older)
    {
        if (!IsVoiceGroupable(newer) || !IsVoiceGroupable(older) || newer.ChannelId != older.ChannelId || newer.SeasonId != older.SeasonId || newer.Scope != older.Scope || newer.ProjectionStatus != older.ProjectionStatus) return false;
        var gap = newer.PeriodStartsAtUtc!.Value - older.PeriodEndsAtUtc!.Value; if (gap.TotalSeconds < -1 || gap.TotalSeconds > 1) return false;
        var newerStart = newer.PeriodStartsAtUtc.GetValueOrDefault(); var newerEnd = newer.PeriodEndsAtUtc.GetValueOrDefault(); var olderStart = older.PeriodStartsAtUtc.GetValueOrDefault(); var olderEnd = older.PeriodEndsAtUtc.GetValueOrDefault();
        var newerRate = newer.Amount / (decimal)(newerEnd - newerStart).TotalMinutes; var olderRate = older.Amount / (decimal)(olderEnd - olderStart).TotalMinutes;
        return decimal.Abs(newerRate - olderRate) <= 0.000001m;
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
    private Cursor? ReadCursor(string? value, ulong guild, ulong user, string filter) { if (string.IsNullOrEmpty(value)) return null; var parts = value.Split('.'); if (parts.Length != 2 || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(parts[1]), HMACSHA256.HashData(cursorKey, Encoding.UTF8.GetBytes(parts[0])))) throw new XpAuditValidationException("xpAudit.invalidCursor"); try { var p = JsonSerializer.Deserialize<Cursor>(Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])))!; if (p.Guild != guild || p.User != user || p.Filter != filter) throw new XpAuditValidationException("xpAudit.invalidCursor"); return p; } catch (FormatException) { throw new XpAuditValidationException("xpAudit.invalidCursor"); } }
    private static string RegexEscape(string value) => System.Text.RegularExpressions.Regex.Escape(value);
    private sealed record Cursor(ulong Guild, ulong User, string Filter, string? Name, ulong UserId, DateTime? OccurredAt, string? Id);
}
