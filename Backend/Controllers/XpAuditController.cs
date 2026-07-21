using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rankoon.Api;
using Rankoon.Data.Auth;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;

namespace Rankoon.Controllers;

public sealed record AdjustmentBody(decimal Amount, XpLedgerScope Scope, string? Reason, string? Reference, string? RequestId);
public sealed record ReverseBody(string? Reason, string? Reference, string? RequestId);

[ApiController]
[Authorize]
[Route("api/guilds/{guildId}/xp-audit")]
public sealed class XpAuditController(IGuildAuthorizationService authorization, IXpAuditService audit) : ControllerBase
{
    [HttpGet("members")]
    public async Task<IActionResult> Members(string guildId, [FromQuery] string? query, [FromQuery] bool includeFormerMembers, [FromQuery] int take = 25, [FromQuery] string? cursor = null)
    {
        if (!ulong.TryParse(guildId, out var id) || id == 0) return this.ApiError("guild.invalidId");
        if (!await authorization.CanAccessModuleAsync(User, id, GuildModuleIds.XpAudit, HttpContext.RequestAborted)) return Forbid();
        try { return Ok(await audit.SearchMembersAsync(id, query, includeFormerMembers, take, cursor, HttpContext.RequestAborted)); } catch (XpAuditValidationException e) { return this.ApiError(e.Code); }
    }
    [HttpGet("members/{userId}")]
    public async Task<IActionResult> Member(string guildId, string userId)
    {
        if (!TryIds(guildId, userId, out var gid, out var uid, out var error)) return error!;
        if (!await authorization.CanAccessModuleAsync(User, gid, GuildModuleIds.XpAudit, HttpContext.RequestAborted)) return Forbid();
        var actor = authorization.GetDiscordUserId(User); var owner = await authorization.IsOwnerAsync(User, gid, HttpContext.RequestAborted); var canAdjust = await authorization.CanAccessModuleAsync(User, gid, GuildModuleIds.XpAdjustments, HttpContext.RequestAborted);
        var details = await audit.GetMemberDetailsAsync(gid, uid, canAdjust && (owner || actor != uid), actor == uid, owner, HttpContext.RequestAborted);
        return details == null ? this.ApiError("xpAudit.memberNotFound") : Ok(details);
    }
    [HttpGet("members/{userId}/entries")]
    public async Task<IActionResult> Entries(string guildId, string userId, [FromQuery] XpAuditEntryFilter filter)
    {
        if (!TryIds(guildId, userId, out var gid, out var uid, out var error)) return error!;
        if (!await authorization.CanAccessModuleAsync(User, gid, GuildModuleIds.XpAudit, HttpContext.RequestAborted)) return Forbid();
        if (filter.From > filter.To) return this.ApiError("xpAudit.invalidFilter");
        try { return Ok(await audit.GetEntriesAsync(gid, uid, filter, HttpContext.RequestAborted)); } catch (XpAuditValidationException e) { return this.ApiError(e.Code); }
    }
    [HttpPost("members/{userId}/adjustments")]
    public async Task<IActionResult> Adjust(string guildId, string userId, [FromBody] AdjustmentBody body)
    {
        if (!TryIds(guildId, userId, out var gid, out var uid, out var error)) return error!;
        if (!await authorization.CanAccessModuleAsync(User, gid, GuildModuleIds.XpAdjustments, HttpContext.RequestAborted)) return Forbid();
        var actor = authorization.GetDiscordUserId(User); if (actor == null) return Forbid(); var owner = await authorization.IsOwnerAsync(User, gid, HttpContext.RequestAborted);
        if (!owner && actor == uid) return this.ApiError("xpAdjustment.selfAdjustmentForbidden");
        if (body.Amount == 0) return this.ApiError("xpAdjustment.amountRequired"); if (Math.Abs(body.Amount) > 1_000_000m || decimal.Round(body.Amount, 4) != body.Amount) return this.ApiError("xpAdjustment.amountOutOfRange");
        var reason = body.Reason?.Trim(); if (string.IsNullOrEmpty(reason)) return this.ApiError("xpAdjustment.reasonRequired"); if (reason.Length < 10) return this.ApiError("xpAdjustment.reasonTooShort"); if (reason.Length > 1000) return this.ApiError("xpAdjustment.reasonTooLong"); var reference = body.Reference?.Trim(); if (reference?.Length > 250) return this.ApiError("xpAdjustment.referenceTooLong");
        if (!Guid.TryParse(body.RequestId, out var requestId)) return this.ApiError("xpAdjustment.invalidRequestId"); if (body.Scope == XpLedgerScope.SeasonOnly) return this.ApiError("xpAudit.invalidFilter");
        var member = await authorization.ResolveMemberAsync(User, gid, HttpContext.RequestAborted); if (member == null) return Forbid();
        try { return Ok(await audit.CreateAdjustmentAsync(gid, uid, actor.Value, member.DisplayName, new(body.Amount, body.Scope, reason, reference, requestId), HttpContext.RequestAborted)); } catch (XpAuditValidationException e) { return this.ApiError(e.Code); } catch (XpAuditConflictException e) { return this.ApiError(e.Code); }
    }
    [HttpPost("entries/{entryId}/reverse")]
    public async Task<IActionResult> Reverse(string guildId, string entryId, [FromBody] ReverseBody body)
    {
        if (!ulong.TryParse(guildId, out var gid) || gid == 0) return this.ApiError("guild.invalidId"); if (!await authorization.CanAccessModuleAsync(User, gid, GuildModuleIds.XpAdjustments, HttpContext.RequestAborted)) return Forbid();
        var reason = body.Reason?.Trim(); if (string.IsNullOrEmpty(reason)) return this.ApiError("xpAdjustment.reasonRequired"); if (reason.Length < 10) return this.ApiError("xpAdjustment.reasonTooShort"); if (reason.Length > 1000) return this.ApiError("xpAdjustment.reasonTooLong"); var reference = body.Reference?.Trim(); if (reference?.Length > 250) return this.ApiError("xpAdjustment.referenceTooLong"); if (!Guid.TryParse(body.RequestId, out var requestId)) return this.ApiError("xpAdjustment.invalidRequestId");
        var actor = authorization.GetDiscordUserId(User); var member = await authorization.ResolveMemberAsync(User, gid, HttpContext.RequestAborted); if (actor == null || member == null) return Forbid();
        try { return Ok(await audit.ReverseAdjustmentAsync(gid, entryId, actor.Value, member.DisplayName, reason, reference, requestId, HttpContext.RequestAborted)); } catch (XpAuditValidationException e) { return this.ApiError(e.Code); } catch (XpAuditConflictException e) { return this.ApiError(e.Code); }
    }
    private bool TryIds(string guildId, string userId, out ulong guild, out ulong user, out IActionResult? error) { error = null; if (!ulong.TryParse(guildId, out guild) || guild == 0) { error = this.ApiError("guild.invalidId"); user = 0; return false; } if (!ulong.TryParse(userId, out user) || user == 0) { error = this.ApiError("xpAudit.invalidUserId"); return false; } return true; }
}
