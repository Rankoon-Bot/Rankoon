using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Api;
using Rankoon.Data.Auth;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Xp;

namespace Rankoon.Controllers;

public sealed record PlanSeasonsRequest(int Count);
public sealed record SeasonBulkResult(long AffectedCount);

[ApiController]
[Authorize]
[Route("api/guilds/{guildId}/xp/seasons")]
public sealed class SeasonController(IGuildAuthorizationService authorization, RankoonDbContext database, ISeasonService seasons, ISeasonLifecycleService lifecycle, SeasonCoordinator coordinator, TimeProvider timeProvider) : ControllerBase
{
    private async Task<(ulong Id, IActionResult? Error)> AuthorizeAsync(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id) || id == 0) return (0, this.ApiError("guild.invalidId"));
        return await authorization.CanAccessModuleAsync(User, id, GuildModuleIds.Xp, HttpContext.RequestAborted) ? (id, null) : (0, Forbid());
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig(string guildId) { var (id, error) = await AuthorizeAsync(guildId); return error ?? Ok(await seasons.GetSettingsAsync(id, HttpContext.RequestAborted)); }

    [HttpPut("config")]
    public async Task<IActionResult> SaveConfig(string guildId, [FromBody] GuildSeasonSettings settings)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        settings.GuildId = id;
        try
        {
            if (!settings.Enabled && await database.GuildSeasons.Find(x => x.GuildId == id && (x.Status == SeasonStatus.Active || x.Status == SeasonStatus.Closing)).AnyAsync(HttpContext.RequestAborted))
                return this.ApiError("season.activeConflict");
            await seasons.SaveSettingsAsync(settings, HttpContext.RequestAborted);
            return Ok(await seasons.GetSettingsAsync(id, HttpContext.RequestAborted));
        }
        catch (SeasonSettingsValidationException exception) { return ValidationError(exception); }
        catch (TimeZoneNotFoundException) { return this.ApiError("season.invalidTimeZone"); }
        catch (ArgumentException) { return this.ApiError("season.invalidSchedule"); }
    }

    [HttpPost("preview")]
    public async Task<IActionResult> Preview(string guildId, [FromBody] GuildSeasonSettings settings, [FromQuery] int count = 3)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        if (count is < 1 or > 24) return this.ApiError("season.invalidSchedule", errors: new Dictionary<string, IReadOnlyList<ApiValidationError>>(StringComparer.Ordinal) { ["count"] = [ApiErrorFactory.Validation("season.invalidSchedule")] });
        settings.GuildId = id;
        try { return Ok(new SeasonScheduleGenerator().Generate(settings, "Guild", 1, count)); }
        catch (SeasonSettingsValidationException exception) { return ValidationError(exception); }
        catch (TimeZoneNotFoundException) { return this.ApiError("season.invalidTimeZone"); }
        catch (ArgumentException) { return this.ApiError("season.invalidSchedule"); }
    }

    [HttpGet]
    public async Task<IActionResult> List(string guildId) { var (id, error) = await AuthorizeAsync(guildId); return error ?? Ok(await seasons.GetSeasonsAsync(id, HttpContext.RequestAborted)); }

    [HttpGet("status")]
    public async Task<IActionResult> Status(string guildId) { var (_, error) = await AuthorizeAsync(guildId); return error ?? Ok(coordinator.Status); }

    [HttpGet("current")]
    public async Task<IActionResult> Current(string guildId)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        var season = await seasons.ResolveAsync(id, timeProvider.GetUtcNow().UtcDateTime, HttpContext.RequestAborted);
        return season == null ? NotFound() : Ok(season);
    }

    [HttpGet("{seasonId}")]
    public async Task<IActionResult> Get(string guildId, string seasonId)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        var season = await database.GuildSeasons.Find(x => x.GuildId == id && x.Id == seasonId).FirstOrDefaultAsync(HttpContext.RequestAborted);
        return season == null ? NotFound() : Ok(season);
    }

    [HttpPost]
    public async Task<IActionResult> Create(string guildId, [FromBody] GuildSeason season)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        if (!ValidSeasonInput(season)) return this.ApiError("season.invalidSchedule");
        var settings = await seasons.GetSettingsAsync(id, HttpContext.RequestAborted);
        if (settings.ScheduleKind == SeasonScheduleKind.Manual && !await IsAdministratorAsync(id)) return Forbid();
        if (await OverlapsAsync(id, season.StartsAtUtc, season.EndsAtUtc, null)) return this.ApiError("season.planConflict");
        var next = await database.GuildSeasons.Find(x => x.GuildId == id).SortByDescending(x => x.Sequence).FirstOrDefaultAsync(HttpContext.RequestAborted);
        season.Id = null; season.GuildId = id; season.Sequence = next?.Sequence + 1 ?? 1; season.Status = SeasonStatus.Scheduled; season.CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        season.SettingsSnapshot = settings;
        await database.GuildSeasons.InsertOneAsync(season, cancellationToken: HttpContext.RequestAborted);
        return Ok(season);
    }

    [HttpPost("plan")]
    public async Task<IActionResult> Plan(string guildId, [FromBody] PlanSeasonsRequest request)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        if (request.Count is < 1 or > 24) return this.ApiError("season.invalidSchedule");
        var settings = await seasons.GetSettingsAsync(id, HttpContext.RequestAborted);
        if (!settings.Enabled) return this.ApiError("season.invalidTransition");
        if (settings.ScheduleKind == SeasonScheduleKind.Manual) return this.ApiError("season.manualSchedule");
        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var existing = await database.GuildSeasons.Find(x => x.GuildId == id).SortBy(x => x.Sequence).ToListAsync(HttpContext.RequestAborted);
            var firstSequence = existing.Count == 0 ? 1 : existing.Max(x => x.Sequence) + 1;
            const int batchSize = 120;
            const int maximumOccurrences = 36_600;
            var generated = new List<SeasonScheduleCandidate>();
            var generator = new SeasonScheduleGenerator();
            for (var offset = 0; offset < maximumOccurrences && generated.Count < request.Count; offset += batchSize)
            {
                HttpContext.RequestAborted.ThrowIfCancellationRequested();
                var batch = generator.Generate(settings, "Guild", offset + 1, Math.Min(batchSize, maximumOccurrences - offset), occurrenceOffset: offset);
                generated.AddRange(batch.Where(candidate => candidate.EndsAtUtc > now && existing.All(season => candidate.EndsAtUtc <= season.StartsAtUtc || candidate.StartsAtUtc >= season.EndsAtUtc)).Take(request.Count - generated.Count));
            }
            if (generated.Count != request.Count) return this.ApiError("season.planConflict");
            var previousSeasonId = existing.OrderByDescending(x => x.Sequence).FirstOrDefault()?.Id;
            var planned = new List<GuildSeason>();
            foreach (var candidate in generated)
            {
                var sequence = firstSequence + planned.Count;
                var name = SeasonNamingService.Format(settings, sequence, candidate.StartsAtUtc, candidate.EndsAtUtc, "Guild");
                var plannedSeason = new GuildSeason { Id = ObjectId.GenerateNewId().ToString(), GuildId = id, Sequence = sequence, Name = name, StartsAtUtc = candidate.StartsAtUtc, EndsAtUtc = candidate.EndsAtUtc, CreatedAtUtc = now, Status = SeasonStatus.Scheduled, ScheduleRevision = settings.Revision, SettingsSnapshot = settings, PreviousSeasonId = previousSeasonId };
                planned.Add(plannedSeason);
                previousSeasonId = plannedSeason.Id;
            }
            if (planned.Count > 0) await database.GuildSeasons.InsertManyAsync(planned, cancellationToken: HttpContext.RequestAborted);
            return Ok(planned);
        }
        catch (TimeZoneNotFoundException) { return this.ApiError("season.invalidTimeZone"); }
        catch (ArgumentException) { return this.ApiError("season.invalidSchedule"); }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { return this.ApiError("season.planConflict"); }
    }

    [HttpPut("{seasonId}")]
    public async Task<IActionResult> Update(string guildId, string seasonId, [FromBody] GuildSeason season)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        if (!ValidSeasonInput(season)) return this.ApiError("season.invalidSchedule");
        var settings = await seasons.GetSettingsAsync(id, HttpContext.RequestAborted);
        if (settings.ScheduleKind == SeasonScheduleKind.Manual && !await IsAdministratorAsync(id)) return Forbid();
        if (await OverlapsAsync(id, season.StartsAtUtc, season.EndsAtUtc, seasonId)) return this.ApiError("season.planConflict");
        var result = await database.GuildSeasons.UpdateOneAsync(x => x.GuildId == id && x.Id == seasonId && x.Status == SeasonStatus.Scheduled,
            Builders<GuildSeason>.Update.Set(x => x.Name, season.Name).Set(x => x.Description, season.Description).Set(x => x.StartsAtUtc, season.StartsAtUtc).Set(x => x.EndsAtUtc, season.EndsAtUtc), cancellationToken: HttpContext.RequestAborted);
        return result.MatchedCount == 0 ? this.ApiError("season.invalidTransition") : Ok(await database.GuildSeasons.Find(x => x.GuildId == id && x.Id == seasonId).FirstOrDefaultAsync(HttpContext.RequestAborted));
    }

    [HttpPost("{seasonId}/start")]
    public async Task<IActionResult> Start(string guildId, string seasonId) => await TransitionAsync(guildId, seasonId, SeasonStatus.Active);
    [HttpPost("{seasonId}/close")]
    public async Task<IActionResult> Close(string guildId, string seasonId) => await TransitionAsync(guildId, seasonId, SeasonStatus.Closing);
    [HttpPost("{seasonId}/cancel")]
    public async Task<IActionResult> Cancel(string guildId, string seasonId) => await TransitionAsync(guildId, seasonId, SeasonStatus.Cancelled);

    [HttpPost("cancel-scheduled")]
    public async Task<IActionResult> CancelScheduled(string guildId)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        return Ok(new SeasonBulkResult(await lifecycle.CancelScheduledAsync(id, HttpContext.RequestAborted)));
    }

    [HttpPost("{seasonId}/resume")]
    public async Task<IActionResult> Resume(string guildId, string seasonId)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        if (!await lifecycle.ResumeAsync(id, seasonId, HttpContext.RequestAborted)) return this.ApiError("season.notResumable");
        return Ok(await database.GuildSeasons.Find(x => x.GuildId == id && x.Id == seasonId).FirstOrDefaultAsync(HttpContext.RequestAborted));
    }

    [HttpDelete("{seasonId}")]
    public async Task<IActionResult> Delete(string guildId, string seasonId)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        return await lifecycle.DeleteCancelledAsync(id, seasonId, HttpContext.RequestAborted) ? NoContent() : this.ApiError("season.invalidTransition");
    }

    [HttpDelete("cancelled")]
    public async Task<IActionResult> DeleteCancelled(string guildId)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        return Ok(new SeasonBulkResult(await lifecycle.DeleteAllCancelledAsync(id, HttpContext.RequestAborted)));
    }

    private async Task<IActionResult> TransitionAsync(string guildId, string seasonId, SeasonStatus target)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        var changed = target switch { SeasonStatus.Active => await lifecycle.ActivateAsync(id, seasonId, HttpContext.RequestAborted), SeasonStatus.Closing => await lifecycle.CloseAsync(id, seasonId, HttpContext.RequestAborted), SeasonStatus.Cancelled => await lifecycle.CancelAsync(id, seasonId, HttpContext.RequestAborted), _ => false };
        if (!changed) return this.ApiError("season.invalidTransition");
        return Ok(await database.GuildSeasons.Find(x => x.GuildId == id && x.Id == seasonId).FirstOrDefaultAsync(HttpContext.RequestAborted));
    }

    private async Task<bool> OverlapsAsync(ulong guildId, DateTime startsAtUtc, DateTime endsAtUtc, string? exceptSeasonId) => await database.GuildSeasons.Find(x => x.GuildId == guildId && x.Id != exceptSeasonId && x.StartsAtUtc < endsAtUtc && startsAtUtc < x.EndsAtUtc).AnyAsync(HttpContext.RequestAborted);
    private async Task<bool> IsAdministratorAsync(ulong guildId) => await authorization.IsOwnerAsync(User, guildId, HttpContext.RequestAborted) || (await authorization.ResolveMemberAsync(User, guildId, HttpContext.RequestAborted))?.GuildPermissions.Administrator == true;
    private static bool ValidSeasonInput(GuildSeason season) => !string.IsNullOrWhiteSpace(season.Name) && season.Name.Length <= 120
        && (season.Description == null || season.Description.Length <= 1000)
        && season.StartsAtUtc.Kind == DateTimeKind.Utc && season.EndsAtUtc.Kind == DateTimeKind.Utc
        && season.EndsAtUtc > season.StartsAtUtc;
    private ObjectResult ValidationError(SeasonSettingsValidationException exception) => this.ApiError("season.invalidSchedule", errors: exception.Errors
        .GroupBy(error => error.Field, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => (IReadOnlyList<ApiValidationError>)group.Select(error => ApiErrorFactory.Validation(error.ErrorKey)).ToArray(), StringComparer.Ordinal));
}
