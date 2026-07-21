using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Rankoon.Api;
using Rankoon.Data.Auth;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Xp;

namespace Rankoon.Controllers;

public sealed record PlanSeasonsRequest(int Count);

[ApiController]
[Authorize]
[Route("api/guilds/{guildId}/xp/seasons")]
public sealed class SeasonController(IGuildAuthorizationService authorization, RankoonDbContext database, ISeasonService seasons, ISeasonLifecycleService lifecycle, SeasonCoordinator coordinator, TimeProvider timeProvider) : ControllerBase
{
    private async Task<(ulong Id, IActionResult? Error)> AuthorizeAsync(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id)) return (0, this.ApiError("guild.invalidId"));
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
        catch (TimeZoneNotFoundException) { return this.ApiError("season.invalidTimeZone"); }
        catch (ArgumentException) { return this.ApiError("season.invalidSchedule"); }
    }

    [HttpPost("preview")]
    public async Task<IActionResult> Preview(string guildId, [FromBody] GuildSeasonSettings settings, [FromQuery] int count = 3)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        settings.GuildId = id;
        try { return Ok(new SeasonScheduleGenerator().Generate(settings, "Guild", 1, Math.Clamp(count, 1, 24))); }
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
        if (season.EndsAtUtc <= season.StartsAtUtc) return this.ApiError("season.invalidSchedule");
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
        if (settings.ScheduleKind == SeasonScheduleKind.Manual) return this.ApiError("season.manualSchedule");
        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var existing = await database.GuildSeasons.Find(x => x.GuildId == id).SortBy(x => x.Sequence).ToListAsync(HttpContext.RequestAborted);
            var generated = new SeasonScheduleGenerator().Generate(settings, "Guild", 1, 120)
                .Where(candidate => candidate.EndsAtUtc > now && existing.All(season => candidate.EndsAtUtc <= season.StartsAtUtc || candidate.StartsAtUtc >= season.EndsAtUtc))
                .Take(request.Count).ToList();
            if (generated.Count != request.Count) return this.ApiError("season.planConflict");
            var firstSequence = existing.Count == 0 ? 1 : existing.Max(x => x.Sequence) + 1;
            var previousSeasonId = existing.OrderByDescending(x => x.Sequence).FirstOrDefault()?.Id;
            var planned = new List<GuildSeason>();
            foreach (var candidate in generated)
            {
                var plannedSeason = new GuildSeason { GuildId = id, Sequence = firstSequence + planned.Count, Name = candidate.Name, StartsAtUtc = candidate.StartsAtUtc, EndsAtUtc = candidate.EndsAtUtc, CreatedAtUtc = now, Status = SeasonStatus.Scheduled, ScheduleRevision = settings.Revision, SettingsSnapshot = settings, PreviousSeasonId = previousSeasonId };
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
        if (season.EndsAtUtc <= season.StartsAtUtc) return this.ApiError("season.invalidSchedule");
        var settings = await seasons.GetSettingsAsync(id, HttpContext.RequestAborted);
        if (settings.ScheduleKind == SeasonScheduleKind.Manual && !await IsAdministratorAsync(id)) return Forbid();
        if (await OverlapsAsync(id, season.StartsAtUtc, season.EndsAtUtc, seasonId)) return this.ApiError("season.planConflict");
        var result = await database.GuildSeasons.UpdateOneAsync(x => x.GuildId == id && x.Id == seasonId && x.Status == SeasonStatus.Scheduled,
            Builders<GuildSeason>.Update.Set(x => x.Name, season.Name).Set(x => x.Description, season.Description).Set(x => x.StartsAtUtc, season.StartsAtUtc).Set(x => x.EndsAtUtc, season.EndsAtUtc), cancellationToken: HttpContext.RequestAborted);
        return result.MatchedCount == 0 ? this.ApiError("season.invalidTransition") : Ok(await database.GuildSeasons.Find(x => x.Id == seasonId).FirstOrDefaultAsync(HttpContext.RequestAborted));
    }

    [HttpPost("{seasonId}/start")]
    public async Task<IActionResult> Start(string guildId, string seasonId) => await TransitionAsync(guildId, seasonId, SeasonStatus.Active);
    [HttpPost("{seasonId}/close")]
    public async Task<IActionResult> Close(string guildId, string seasonId) => await TransitionAsync(guildId, seasonId, SeasonStatus.Closing);
    [HttpPost("{seasonId}/cancel")]
    public async Task<IActionResult> Cancel(string guildId, string seasonId) => await TransitionAsync(guildId, seasonId, SeasonStatus.Cancelled);

    [HttpPost("{seasonId}/resume")]
    public async Task<IActionResult> Resume(string guildId, string seasonId)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        if (!await lifecycle.ResumeAsync(id, seasonId, HttpContext.RequestAborted)) return this.ApiError("season.notResumable");
        return Ok(await database.GuildSeasons.Find(x => x.Id == seasonId).FirstOrDefaultAsync(HttpContext.RequestAborted));
    }

    [HttpDelete("{seasonId}")]
    public async Task<IActionResult> Delete(string guildId, string seasonId)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        var result = await database.GuildSeasons.DeleteOneAsync(x => x.GuildId == id && x.Id == seasonId && (x.Status == SeasonStatus.Scheduled || x.Status == SeasonStatus.Cancelled), HttpContext.RequestAborted);
        return result.DeletedCount == 0 ? this.ApiError("season.invalidTransition") : NoContent();
    }

    private async Task<IActionResult> TransitionAsync(string guildId, string seasonId, SeasonStatus target)
    {
        var (id, error) = await AuthorizeAsync(guildId); if (error != null) return error;
        var changed = target switch { SeasonStatus.Active => await lifecycle.ActivateAsync(id, seasonId, HttpContext.RequestAborted), SeasonStatus.Closing => await lifecycle.CloseAsync(id, seasonId, HttpContext.RequestAborted), SeasonStatus.Cancelled => await lifecycle.CancelAsync(id, seasonId, HttpContext.RequestAborted), _ => false };
        if (!changed) return this.ApiError("season.invalidTransition");
        return Ok(await database.GuildSeasons.Find(x => x.Id == seasonId).FirstOrDefaultAsync(HttpContext.RequestAborted));
    }

    private async Task<bool> OverlapsAsync(ulong guildId, DateTime startsAtUtc, DateTime endsAtUtc, string? exceptSeasonId) => await database.GuildSeasons.Find(x => x.GuildId == guildId && x.Id != exceptSeasonId && x.StartsAtUtc < endsAtUtc && startsAtUtc < x.EndsAtUtc).AnyAsync(HttpContext.RequestAborted);
    private async Task<bool> IsAdministratorAsync(ulong guildId) => await authorization.IsOwnerAsync(User, guildId, HttpContext.RequestAborted) || (await authorization.ResolveMemberAsync(User, guildId, HttpContext.RequestAborted))?.GuildPermissions.Administrator == true;
}
