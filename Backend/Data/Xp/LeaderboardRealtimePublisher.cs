using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Hubs;

namespace Rankoon.Data.Xp;

public sealed record LeaderboardChanged(string Alias, SeasonLeaderboardScope Scope, string? SeasonId, LeaderboardVisibility Visibility);
public sealed record LeaderboardEntryChanged(string Alias, SeasonLeaderboardScope Scope, string? SeasonId, string Operation, string UserId, LeaderboardEntryDto? Entry);

public interface ILeaderboardRealtimePublisher
{
    Task PublishMemberAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default);
    Task PublishGuildAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task PublishSettingsAsync(GuildLeaderboardSettings settings, string? previousAlias = null, CancellationToken cancellationToken = default);
}

public sealed class LeaderboardRealtimePublisher(RankoonDbContext database, IHubContext<LeaderboardHub> hub, LeaderboardSubscriptionRegistry subscriptions, TimeProvider timeProvider) : ILeaderboardRealtimePublisher
{
    private readonly ConcurrentDictionary<(ulong GuildId, string Alias, SeasonLeaderboardScope Scope, string? SeasonId), CancellationTokenSource> pending = new();

    public async Task PublishMemberAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default)
    {
        var settings = await database.GuildLeaderboardSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (settings == null) return;

        var member = await database.MemberXp.Find(x => x.GuildId == guildId && x.UserId == userId).FirstOrDefaultAsync(cancellationToken);
        if (member?.IsCurrentMember != true)
        {
            foreach (var subscription in subscriptions.RemoveMemberSubscriptions(guildId, userId))
            {
                await hub.Groups.RemoveFromGroupAsync(subscription.ConnectionId, subscription.Group, cancellationToken);
                if (!subscriptions.HasAudience(subscription.ConnectionId, subscription.AudienceGroup))
                    await hub.Groups.RemoveFromGroupAsync(subscription.ConnectionId, subscription.AudienceGroup, cancellationToken);
                await hub.Clients.Client(subscription.ConnectionId).SendAsync("leaderboardAccessRevoked", new LeaderboardChanged(settings.Alias, subscription.Scope, subscription.SeasonId, settings.Visibility), cancellationToken);
            }
        }

        var scopes = new List<(SeasonLeaderboardScope Scope, string? SeasonId)> { (SeasonLeaderboardScope.Lifetime, null) };
        if (await database.GuildSeasons.Find(x => x.GuildId == guildId && x.Status == SeasonStatus.Active).AnyAsync(cancellationToken)) scopes.Add((SeasonLeaderboardScope.CurrentSeason, null));
        scopes.AddRange(subscriptions.GetGuildSubscriptions(guildId).Select(subscription => (subscription.Scope, subscription.SeasonId)));
        foreach (var scope in scopes.Distinct()) await PublishInvalidationAsync(settings, scope.Scope, scope.SeasonId, cancellationToken);
    }

    public async Task PublishGuildAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var settings = await database.GuildLeaderboardSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (settings == null) return;

        Queue(settings, SeasonLeaderboardScope.Lifetime, null);
        if (await database.GuildSeasons.Find(x => x.GuildId == guildId && x.Status == SeasonStatus.Active).AnyAsync(cancellationToken))
            Queue(settings, SeasonLeaderboardScope.CurrentSeason, null);
        foreach (var subscription in subscriptions.GetGuildSubscriptions(guildId))
            Queue(settings, subscription.Scope, subscription.SeasonId);
    }

    private async Task PublishInvalidationAsync(GuildLeaderboardSettings settings, SeasonLeaderboardScope scope, string? seasonId, CancellationToken cancellationToken)
    {
        var change = new LeaderboardChanged(settings.Alias, scope, seasonId, settings.Visibility);
        await hub.Clients.Group(HubGroupNames.Leaderboard("members", settings.Alias, scope, seasonId)).SendAsync("leaderboardInvalidated", change, cancellationToken);
        await hub.Clients.Group(HubGroupNames.Leaderboard("public", settings.Alias, scope, seasonId)).SendAsync("leaderboardInvalidated", change, cancellationToken);
    }

    public Task PublishSettingsAsync(GuildLeaderboardSettings settings, string? previousAlias = null, CancellationToken cancellationToken = default)
    {
        Queue(settings, SeasonLeaderboardScope.Lifetime, null, previousAlias);
        foreach (var subscription in subscriptions.GetGuildSubscriptions(settings.GuildId))
            Queue(settings, subscription.Scope, subscription.SeasonId, previousAlias);
        return Task.CompletedTask;
    }

    private void Queue(GuildLeaderboardSettings settings, SeasonLeaderboardScope scope, string? seasonId, string? previousAlias = null)
    {
        var alias = previousAlias ?? settings.Alias;
        var key = (settings.GuildId, alias, scope, seasonId);
        var cancellation = new CancellationTokenSource();
        if (pending.TryGetValue(key, out var existing)) existing.Cancel();
        pending[key] = cancellation;
        _ = PublishDebouncedAsync(key, settings, cancellation);
    }

    private async Task PublishDebouncedAsync((ulong GuildId, string Alias, SeasonLeaderboardScope Scope, string? SeasonId) key, GuildLeaderboardSettings settings, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, cancellation.Token);
            await hub.Clients.Group(HubGroupNames.Leaderboard("public", key.Alias, key.Scope, key.SeasonId))
                .SendAsync("leaderboardChanged", new LeaderboardChanged(key.Alias, key.Scope, key.SeasonId, settings.Visibility));
            await hub.Clients.Group(HubGroupNames.Leaderboard("members", key.Alias, key.Scope, key.SeasonId))
                .SendAsync("leaderboardChanged", new LeaderboardChanged(key.Alias, key.Scope, key.SeasonId, settings.Visibility));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, cancellation))
                pending.TryRemove(key, out _);
            cancellation.Dispose();
        }
    }
}
