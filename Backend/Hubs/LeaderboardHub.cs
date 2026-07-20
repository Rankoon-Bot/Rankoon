using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Rankoon.Data.Auth;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;

namespace Rankoon.Hubs;

[AllowAnonymous]
public sealed class LeaderboardHub(LeaderboardService leaderboard, IGuildAuthorizationService authorization, LeaderboardSubscriptionRegistry subscriptions) : Hub
{
    public async Task Subscribe(string alias, SeasonLeaderboardScope scope = SeasonLeaderboardScope.Lifetime, string? seasonId = null)
    {
        var settings = await leaderboard.FindSettingsAsync(alias, Context.ConnectionAborted)
            ?? throw new HubException("Leaderboard not found.");
        var isMember = Context.User?.Identity?.IsAuthenticated == true &&
            await authorization.IsMemberAsync(Context.User, settings.GuildId, Context.ConnectionAborted);
        if (settings.Visibility == LeaderboardVisibility.MembersOnly && !isMember)
            throw new HubException("Leaderboard access denied.");
        if (!await leaderboard.IsScopeAvailableAsync(settings, scope, seasonId, Context.ConnectionAborted))
            throw new HubException("Leaderboard scope is unavailable.");

        var audience = isMember ? "members" : "public";
        var audienceGroup = HubGroupNames.LeaderboardAudience(audience, settings.Alias);
        var group = HubGroupNames.Leaderboard(audience, settings.Alias, scope, seasonId);
        await Groups.AddToGroupAsync(Context.ConnectionId, audienceGroup, Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        var userId = Context.User == null ? null : authorization.GetDiscordUserId(Context.User);
        subscriptions.Add(new(Context.ConnectionId, settings.GuildId, userId, audience, group, audienceGroup));
    }

    public async Task Unsubscribe(string alias, SeasonLeaderboardScope scope = SeasonLeaderboardScope.Lifetime, string? seasonId = null)
    {
        var settings = await leaderboard.FindSettingsAsync(alias, Context.ConnectionAborted);
        if (settings == null) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroupNames.LeaderboardAudience("public", settings.Alias), Context.ConnectionAborted);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroupNames.LeaderboardAudience("members", settings.Alias), Context.ConnectionAborted);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroupNames.Leaderboard("public", settings.Alias, scope, seasonId), Context.ConnectionAborted);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroupNames.Leaderboard("members", settings.Alias, scope, seasonId), Context.ConnectionAborted);
        subscriptions.Remove(Context.ConnectionId, HubGroupNames.Leaderboard("public", settings.Alias, scope, seasonId));
        subscriptions.Remove(Context.ConnectionId, HubGroupNames.Leaderboard("members", settings.Alias, scope, seasonId));
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
