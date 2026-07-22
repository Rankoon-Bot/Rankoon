using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rankoon.Api;
using Rankoon.Data.Auth;
using Rankoon.Data.Development;

namespace Rankoon.Controllers;

public sealed record GenerateDevelopmentLeaderboardRequest(int Count);
public sealed record TriggerDevelopmentXpEventsRequest(int Count, int MinimumXp = 5, int MaximumXp = 100);

[ApiController]
[Authorize]
[DevelopmentOnly]
[Route("api/dev/guilds/{guildId}/leaderboard-mocks")]
public sealed class DevelopmentLeaderboardController(IWebHostEnvironment environment, IGuildAuthorizationService authorization, DevelopmentLeaderboardService development) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Status(string guildId) => await ExecuteAsync(guildId, development.GetStatusAsync);

    [HttpPost]
    public async Task<IActionResult> Generate(string guildId, [FromBody] GenerateDevelopmentLeaderboardRequest request) =>
        await ExecuteAsync(guildId, (id, cancellationToken) => development.GenerateAsync(id, request.Count, cancellationToken));

    [HttpPost("events")]
    public async Task<IActionResult> Events(string guildId, [FromBody] TriggerDevelopmentXpEventsRequest request) =>
        await ExecuteAsync(guildId, (id, cancellationToken) => development.TriggerEventsAsync(id, request.Count, request.MinimumXp, request.MaximumXp, cancellationToken));

    [HttpDelete]
    public async Task<IActionResult> Remove(string guildId) => await ExecuteAsync(guildId, development.RemoveAsync);

    private async Task<IActionResult> ExecuteAsync<T>(string guildId, Func<ulong, CancellationToken, Task<T>> operation)
    {
        if (!environment.IsDevelopment()) return NotFound();
        if (!ulong.TryParse(guildId, out var id) || id == 0) return this.ApiError("guild.invalidId");
        if (!await authorization.IsOwnerAsync(User, id, HttpContext.RequestAborted)) return Forbid();
        try { return Ok(await operation(id, HttpContext.RequestAborted)); }
        catch (ArgumentOutOfRangeException) { return BadRequest(); }
        catch (InvalidOperationException exception) { return Conflict(new { message = exception.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
}
