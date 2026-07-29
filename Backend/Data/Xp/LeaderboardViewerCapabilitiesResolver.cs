using System.Security.Claims;
using Rankoon.Data.Auth;

namespace Rankoon.Data.Xp;

internal static class LeaderboardViewerCapabilitiesResolver
{
    internal static async Task<LeaderboardViewerCapabilitiesDto?> ResolveAsync(
        IGuildAuthorizationService authorization,
        ClaimsPrincipal? user,
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        if (user?.Identity?.IsAuthenticated != true) return null;

        var moduleIds = await authorization.GetAccessibleModuleIdsAsync(user, guildId, cancellationToken);
        var xpAudit = moduleIds.Contains(GuildModuleIds.XpAudit, StringComparer.Ordinal);
        var xpAdjustments = moduleIds.Contains(GuildModuleIds.XpAdjustments, StringComparer.Ordinal);
        return xpAudit || xpAdjustments ? new(guildId.ToString(), xpAudit, xpAdjustments) : null;
    }
}
