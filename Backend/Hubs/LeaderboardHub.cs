using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Rankoon.Data.Auth;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;

namespace Rankoon.Hubs;

public sealed record LeaderboardWindowRequest(string Alias, SeasonLeaderboardScope? Scope, string? SeasonId, int Offset, int Take, bool AroundCurrentUser, IReadOnlyList<string>? CachedUserIds);

[AllowAnonymous]
public sealed class LeaderboardHub(LeaderboardService leaderboard, IGuildAuthorizationService authorization, LeaderboardSubscriptionRegistry subscriptions) : Hub
{
    public async Task Subscribe(string alias, SeasonLeaderboardScope? scope = null, string? seasonId = null)
    {
        var settings = await leaderboard.FindSettingsAsync(alias, Context.ConnectionAborted)
            ?? throw new HubException("Leaderboard not found.");
        var isMember = Context.User?.Identity?.IsAuthenticated == true &&
            await authorization.IsMemberAsync(Context.User, settings.GuildId, Context.ConnectionAborted);
        if (settings.Visibility == LeaderboardVisibility.MembersOnly && !isMember)
            throw new HubException("Leaderboard access denied.");
        var resolvedScope = await leaderboard.ResolveScopeAsync(settings, scope, seasonId, Context.ConnectionAborted);
        if (!await leaderboard.IsScopeAvailableAsync(settings, resolvedScope, seasonId, Context.ConnectionAborted))
            throw new HubException("Leaderboard scope is unavailable.");

        var audience = isMember ? "members" : "public";
        var audienceGroup = HubGroupNames.LeaderboardAudience(audience, settings.Alias);
        var group = HubGroupNames.Leaderboard(audience, settings.Alias, resolvedScope, seasonId);
        await Groups.AddToGroupAsync(Context.ConnectionId, audienceGroup, Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        var userId = Context.User == null ? null : authorization.GetDiscordUserId(Context.User);
        subscriptions.Add(new(Context.ConnectionId, settings.GuildId, userId, audience, group, audienceGroup, resolvedScope, seasonId));
    }

    public async Task Unsubscribe(string alias, SeasonLeaderboardScope scope = SeasonLeaderboardScope.Lifetime, string? seasonId = null)
    {
        var settings = await leaderboard.FindSettingsAsync(alias, Context.ConnectionAborted);
        if (settings == null) return;
        foreach (var audience in new[] { "public", "members" })
        {
            var group = HubGroupNames.Leaderboard(audience, settings.Alias, scope, seasonId);
            var subscription = subscriptions.Remove(Context.ConnectionId, group);
            if (subscription == null) continue;
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
            if (!subscriptions.HasAudience(Context.ConnectionId, subscription.AudienceGroup))
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, subscription.AudienceGroup, Context.ConnectionAborted);
        }
    }

    public async Task<LeaderboardWindowDto> GetWindow(LeaderboardWindowRequest request)
    {
        if (request.Take is < 10 or > 100 || request.Offset < 0 || request.CachedUserIds?.Count > 100)
            throw new HubException("Invalid leaderboard window.");
        var settings = await leaderboard.FindSettingsAsync(request.Alias, Context.ConnectionAborted)
            ?? throw new HubException("Leaderboard not found.");
        var isMember = Context.User?.Identity?.IsAuthenticated == true &&
            await authorization.IsMemberAsync(Context.User, settings.GuildId, Context.ConnectionAborted);
        if (settings.Visibility == LeaderboardVisibility.MembersOnly && !isMember)
            throw new HubException("Leaderboard access denied.");
        var scope = await leaderboard.ResolveScopeAsync(settings, request.Scope, request.SeasonId, Context.ConnectionAborted);
        if (!await leaderboard.IsScopeAvailableAsync(settings, scope, request.SeasonId, Context.ConnectionAborted))
            throw new HubException("Leaderboard scope is unavailable.");
        var audience = isMember ? "members" : "public";
        var group = HubGroupNames.Leaderboard(audience, settings.Alias, scope, request.SeasonId);
        if (subscriptions.Get(Context.ConnectionId, group) == null) throw new HubException("Leaderboard subscription is required.");
        var userId = Context.User == null ? null : authorization.GetDiscordUserId(Context.User);
        var cachedIds = request.CachedUserIds?.Select(value => ulong.TryParse(value, out var id) ? id : 0).Where(id => id != 0).Distinct().ToArray() ?? [];
        return await leaderboard.GetWindowAsync(settings, isMember, userId, scope, request.SeasonId, request.Offset, request.Take, request.AroundCurrentUser && isMember, cachedIds, Context.ConnectionAborted);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
