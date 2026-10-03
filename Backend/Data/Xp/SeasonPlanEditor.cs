using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Rankoon.Data.Model;

namespace Rankoon.Data.Xp;

public sealed record SeasonPlanChangeRequest(string Kind, string? SeasonId = null, string FollowUp = "KeepDates", string? Name = null,
    DateTime? StartsAtUtc = null, DateTime? EndsAtUtc = null, string? OperationId = null, string? ExpectedPlanToken = null);
public sealed record SeasonPlanChangeRow(GuildSeason Before, GuildSeason? After);
public sealed record SeasonPlanChangePreview(string PlanToken, IReadOnlyList<SeasonPlanChangeRow> Changes);
public sealed class SeasonPlanChangeOperation
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public ulong GuildId { get; set; }
    public string RequestJson { get; set; } = "";
    public bool Completed { get; set; }
    public List<string> DeletedIds { get; set; } = [];
    public List<GuildSeason> Updated { get; set; } = [];
    public GuildSeasonSettings Settings { get; set; } = new();
}

public static class SeasonPlanEditor
{
    public static bool IsDraft(GuildSeason s) => s.Status is SeasonStatus.Scheduled or SeasonStatus.Cancelled or SeasonStatus.Paused
        && s.ActivatedAtUtc == null && !s.BaselineInitialized && !s.CarryOverApplied && !s.Finalized;
    public static string Token(GuildSeasonSettings settings, IEnumerable<GuildSeason> seasons) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { settings, seasons = seasons.OrderBy(s => s.Id).ToArray() }))));
    public static SeasonPlanChangePreview Preview(GuildSeasonSettings settings, IReadOnlyList<GuildSeason> existing, SeasonPlanChangeRequest request, DateTime now)
    {
        if (request.Kind is not ("Delete" or "DeleteScheduled" or "Update" or "Pause" or "ResumePause") || request.FollowUp is not ("KeepDates" or "Shift" or "Delete")) throw new ArgumentException("Invalid plan action.");
        var ordered = existing.OrderBy(s => s.StartsAtUtc).ThenBy(s => s.Sequence).ToList();
        var target = ordered.FirstOrDefault(s => s.Id == request.SeasonId);
        if (request.Kind != "DeleteScheduled" && (target == null || !IsDraft(target))) throw new SeasonPlanningConflictException();
        if (request.Kind == "Update" && target!.Status != SeasonStatus.Scheduled) throw new SeasonPlanningConflictException();
        if (request.Kind == "Pause" && target!.Status != SeasonStatus.Scheduled || request.Kind == "ResumePause" && (target!.Status != SeasonStatus.Paused || target.EndsAtUtc <= now)) throw new SeasonPlanningConflictException();
        if (request.Kind is "Pause" or "ResumePause" && request.FollowUp != "KeepDates") throw new ArgumentException("A pause preserves following dates.");
        var after = existing.ToDictionary(s => s.Id!, s => JsonSerializer.Deserialize<GuildSeason>(JsonSerializer.Serialize(s))!);
        var following = target == null ? [] : ordered.Skip(ordered.IndexOf(target) + 1).Where(s => IsDraft(s) && s.Status != SeasonStatus.Paused).ToList();
        if (request.Kind == "DeleteScheduled")
        {
            foreach (var s in ordered.Where(s => s.Status == SeasonStatus.Scheduled && IsDraft(s))) after.Remove(s.Id!);
        }
        else
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
            var delta = TimeSpan.Zero;
            if (request.Kind == "Update")
            {
                if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120 || request.StartsAtUtc?.Kind != DateTimeKind.Utc
                    || request.EndsAtUtc?.Kind != DateTimeKind.Utc || request.EndsAtUtc <= request.StartsAtUtc || request.EndsAtUtc <= now) throw new ArgumentException("Invalid season period.");
                var edited = after[target!.Id!];
                edited.Name = request.Name.Trim();
                if (edited.Name != target.Name) edited.AutomaticallyNamed = false;
                edited.StartsAtUtc = request.StartsAtUtc!.Value; edited.EndsAtUtc = request.EndsAtUtc!.Value;
                delta = TimeZoneInfo.ConvertTimeFromUtc(edited.EndsAtUtc, zone) - TimeZoneInfo.ConvertTimeFromUtc(target.EndsAtUtc, zone);
                edited.ScheduleOccurrence = null;
            }
            else if (request.Kind is "Pause" or "ResumePause")
            {
                if (request.Name?.Length > 120) throw new ArgumentException("Pause name too long.");
                after[target!.Id!].Status = request.Kind == "Pause" ? SeasonStatus.Paused : SeasonStatus.Scheduled;
                after[target.Id!].PauseLabel = request.Kind == "Pause" ? (string.IsNullOrWhiteSpace(request.Name) ? "Pause" : request.Name.Trim()) : null;
            }
            else
            {
                after.Remove(target!.Id!);
                var nextScheduled = following.FirstOrDefault(s => s.Status == SeasonStatus.Scheduled);
                if (nextScheduled != null) delta = TimeZoneInfo.ConvertTimeFromUtc(target.StartsAtUtc, zone) - TimeZoneInfo.ConvertTimeFromUtc(nextScheduled.StartsAtUtc, zone);
            }
            foreach (var s in following)
            {
                if (request.FollowUp == "Delete") after.Remove(s.Id!);
                else if (request.FollowUp == "Shift" && s.Status == SeasonStatus.Scheduled)
                {
                    var shifted = after[s.Id!];
                    shifted.StartsAtUtc = SeasonScheduleGenerator.ToUtc(TimeZoneInfo.ConvertTimeFromUtc(s.StartsAtUtc, zone) + delta, zone);
                    shifted.EndsAtUtc = SeasonScheduleGenerator.ToUtc(TimeZoneInfo.ConvertTimeFromUtc(s.EndsAtUtc, zone) + delta, zone);
                    shifted.ScheduleOccurrence = null;
                    if (shifted.EndsAtUtc <= now) throw new SeasonPlanningConflictException();
                }
            }
        }
        var periods = after.Values.Where(s => s.Status != SeasonStatus.Cancelled).OrderBy(s => s.StartsAtUtc).ToList();
        if (periods.Any(s => s.EndsAtUtc <= s.StartsAtUtc)) throw new SeasonPlanningConflictException();
        for (var i = 1; i < periods.Count; i++) if (periods[i].StartsAtUtc < periods[i - 1].EndsAtUtc) throw new SeasonPlanningConflictException();
        var next = SeasonSchedulePlanner.NextCompletedNumber(settings, existing) + existing.LongCount(s => s.NumberingEpoch == settings.NumberingEpoch && s.Status is SeasonStatus.Active or SeasonStatus.Closing);
        foreach (var s in after.Values.Where(s => s.Status == SeasonStatus.Scheduled).OrderBy(s => s.StartsAtUtc).ThenBy(s => s.Sequence))
        {
            s.Number = next++; s.NumberingEpoch = settings.NumberingEpoch;
            if (s.AutomaticallyNamed) s.Name = SeasonNamingService.Format(s.SettingsSnapshot, s.Number.Value, s.StartsAtUtc, s.EndsAtUtc, "Guild");
        }
        var rows = ordered.Select(s => new SeasonPlanChangeRow(s, after.GetValueOrDefault(s.Id!)))
            .Where(r => r.After == null || JsonSerializer.Serialize(r.Before) != JsonSerializer.Serialize(r.After)).ToList();
        // Never remove a predecessor referenced by retained history or live XP.
        var deleted = rows.Where(r => r.After == null).Select(r => r.Before.Id).ToHashSet();
        if (after.Values.Any(s => deleted.Contains(s.PreviousSeasonId))) throw new SeasonPlanningConflictException();
        return new(Token(settings, existing), rows);
    }
}

public sealed partial class SeasonPlanningService
{
    public async Task<SeasonPlanChangePreview> PreviewChangeUnderLeaseAsync(ulong guildId, SeasonPlanChangeRequest request, CancellationToken ct)
    {
        var settings = await seasonSettings.GetSettingsAsync(guildId, ct);
        var existing = await database.GuildSeasons.Find(s => s.GuildId == guildId).ToListAsync(ct);
        return SeasonPlanEditor.Preview(settings, existing, request, timeProvider.GetUtcNow().UtcDateTime);
    }
    public async Task<SeasonSetupResult> ApplyChangeUnderLeaseAsync(ulong guildId, SeasonPlanChangeRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.OperationId, out var operationId)) throw new ArgumentException("Operation id required.");
        var id = operationId.ToString("N"); // Idempotency key scoped by guild through a deterministic ObjectId hash.
        var journalId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{guildId}:{id}"))).Substring(0, 24).ToLowerInvariant();
        var requestJson = JsonSerializer.Serialize(request);
        var operation = await database.SeasonPlanChanges.Find(x => x.Id == journalId).FirstOrDefaultAsync(ct);
        if (operation != null && operation.RequestJson != requestJson) throw new SeasonPlanningConflictException();
        if (operation == null)
        {
            var preview = await PreviewChangeUnderLeaseAsync(guildId, request, ct);
            if (preview.PlanToken != request.ExpectedPlanToken) throw new SeasonPlanningConflictException();
            var deletedIds = preview.Changes.Where(r => r.After == null).Select(r => r.Before.Id!).ToList();
            if (await database.SeasonMemberXp.Find(x => x.GuildId == guildId && deletedIds.Contains(x.SeasonId)).AnyAsync(ct)
                || await database.XpLedger.Find(x => x.GuildId == guildId && deletedIds.Contains(x.SeasonId!)).AnyAsync(ct)
                || await database.SeasonFinalStandings.Find(x => x.GuildId == guildId && deletedIds.Contains(x.SeasonId)).AnyAsync(ct)) throw new SeasonPlanningConflictException();
            var settings = await seasonSettings.GetSettingsAsync(guildId, ct);
            settings.PlanningMode = SeasonPlanningMode.Explicit;
            settings.Revision++;
            settings.NextSeasonStartUtc = null;
            settings.NextScheduleOccurrenceAfterDeletion = 0;
            settings.NextSequenceAfterDeletion = Math.Max(settings.NextSequenceAfterDeletion, preview.Changes.Select(r => r.Before.Sequence + 1).DefaultIfEmpty(1).Max());
            operation = new() { Id = journalId, GuildId = guildId, RequestJson = requestJson, DeletedIds = deletedIds,
                Updated = preview.Changes.Where(r => r.After != null).Select(r => r.After!).ToList(), Settings = settings };
            await database.SeasonPlanChanges.InsertOneAsync(operation, cancellationToken: ct);
        }
        if (!operation.Completed) await CompletePlanChangeAsync(operation, ct);
        return new(await seasonSettings.GetSettingsAsync(guildId, ct), await database.GuildSeasons.Find(s => s.GuildId == guildId).ToListAsync(ct));
    }
    private async Task RecoverPlanChangesAsync(ulong guildId, CancellationToken ct)
    {
        foreach (var op in await database.SeasonPlanChanges.Find(x => x.GuildId == guildId && !x.Completed).ToListAsync(ct)) await CompletePlanChangeAsync(op, ct);
    }
    private async Task CompletePlanChangeAsync(SeasonPlanChangeOperation op, CancellationToken ct)
    {
        // Persist the complete intent first. The shared lease fences all lifecycle work until recovery finishes.
        await database.GuildSeasons.DeleteManyAsync(x => x.GuildId == op.GuildId && op.DeletedIds.Contains(x.Id!), ct);
        foreach (var season in op.Updated) await database.GuildSeasons.ReplaceOneAsync(x => x.GuildId == op.GuildId && x.Id == season.Id, season, cancellationToken: ct);
        await database.GuildSeasonSettings.UpdateOneAsync(x => x.GuildId == op.GuildId,
            Builders<GuildSeasonSettings>.Update.Set(x => x.PlanningMode, SeasonPlanningMode.Explicit).Set(x => x.Revision, op.Settings.Revision)
                .Set(x => x.NextSeasonStartUtc, (DateTime?)null).Set(x => x.NextScheduleOccurrenceAfterDeletion, 0)
                .Max(x => x.NextSequenceAfterDeletion, op.Settings.NextSequenceAfterDeletion).Set(x => x.UpdatedAtUtc, timeProvider.GetUtcNow().UtcDateTime), cancellationToken: ct);
        await database.SeasonPlanChanges.UpdateOneAsync(x => x.Id == op.Id, Builders<SeasonPlanChangeOperation>.Update.Set(x => x.Completed, true), cancellationToken: ct);
    }
}
