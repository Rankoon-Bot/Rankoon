using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rankoon.Api;
using Rankoon.Data.Auth;
using Rankoon.Data.Discord;

namespace Rankoon.Controllers;

public sealed record CustomBotTokenRequest(string? Token, long? Revision);
public sealed record CustomBotRevisionRequest(long? Revision);

[ApiController]
[Authorize]
[Route("api/guilds/{guildId}/custom-bot-identity")]
public sealed class CustomBotIdentityController(IGuildAuthorizationService authorization, ICustomBotIdentityAccessPolicy policy, ICustomBotIdentityService identities) : ControllerBase
{
    [HttpGet("access")]
    public async Task<IActionResult> Access(string guildId)
    {
        var id = await AuthorizeOwnerAsync(guildId); if (id.Error != null) return id.Error;
        return Ok(await policy.EvaluateAsync(id.GuildId, HttpContext.RequestAborted));
    }

    [HttpGet]
    public async Task<IActionResult> Get(string guildId)
    {
        var id = await AuthorizeOwnerAsync(guildId); if (id.Error != null) return id.Error;
        return Ok(await identities.GetAsync(id.GuildId, HttpContext.RequestAborted));
    }

    [HttpPost("token"), HttpPut("token")]
    public async Task<IActionResult> StoreToken(string guildId, [FromBody] CustomBotTokenRequest request)
    {
        var id = await AuthorizeOwnerAsync(guildId); if (id.Error != null) return id.Error;
        if (string.IsNullOrWhiteSpace(request.Token)) return this.ApiError("customBotIdentity.tokenInvalid");
        return ToResult(await identities.StoreTokenAsync(id.GuildId, id.UserId, request.Token, request.Revision, HttpContext.RequestAborted));
    }

    [HttpGet("install-url")]
    public async Task<IActionResult> InstallUrl(string guildId)
    {
        var id = await AuthorizeOwnerAsync(guildId); if (id.Error != null) return id.Error;
        return ToResult(await identities.GetInstallUrlAsync(id.GuildId, HttpContext.RequestAborted));
    }

    [HttpPost("validate")]
    public async Task<IActionResult> Validate(string guildId)
    {
        var id = await AuthorizeOwnerAsync(guildId); if (id.Error != null) return id.Error;
        return ToResult(await identities.ValidateAsync(id.GuildId, HttpContext.RequestAborted));
    }

    [HttpPost("activate")]
    public async Task<IActionResult> Activate(string guildId, [FromBody] CustomBotRevisionRequest request)
    {
        var id = await AuthorizeOwnerAsync(guildId); if (id.Error != null) return id.Error;
        return ToResult(await identities.ActivateAsync(id.GuildId, id.UserId, request.Revision, HttpContext.RequestAborted));
    }

    [HttpPost("restart")]
    public async Task<IActionResult> Restart(string guildId)
    {
        var id = await AuthorizeOwnerAsync(guildId); if (id.Error != null) return id.Error;
        return ToResult(await identities.RestartAsync(id.GuildId, HttpContext.RequestAborted));
    }

    [HttpPost("complete-handover")]
    public async Task<IActionResult> CompleteHandover(string guildId)
    {
        var id = await AuthorizeOwnerAsync(guildId); if (id.Error != null) return id.Error;
        return ToResult(await identities.CompleteHandoverAsync(id.GuildId, HttpContext.RequestAborted));
    }

    [HttpPost("deactivate")]
    public async Task<IActionResult> Deactivate(string guildId)
    {
        var id = await AuthorizeOwnerAsync(guildId); if (id.Error != null) return id.Error;
        return ToResult(await identities.DeactivateAsync(id.GuildId, HttpContext.RequestAborted));
    }

    [HttpDelete]
    public async Task<IActionResult> Delete(string guildId)
    {
        var id = await AuthorizeOwnerAsync(guildId); if (id.Error != null) return id.Error;
        var result = await identities.DeleteAsync(id.GuildId, HttpContext.RequestAborted);
        return result.Succeeded ? NoContent() : ToResult(result);
    }

    private async Task<(ulong GuildId, ulong UserId, IActionResult? Error)> AuthorizeOwnerAsync(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id)) return (0, 0, this.ApiError("guild.invalidId"));
        var userId = authorization.GetDiscordUserId(User);
        if (userId == null || !await authorization.IsOwnerAsync(User, id, HttpContext.RequestAborted)) return (0, 0, this.ApiError("customBotIdentity.ownerRequired"));
        return (id, userId.Value, null);
    }
    private IActionResult ToResult(CustomBotOperationResult result) => result.Succeeded ? Ok(result) : this.ApiError(result.ErrorCode!, result.Diagnostics == null ? null : new Dictionary<string, object?> { ["diagnostics"] = result.Diagnostics });
}
