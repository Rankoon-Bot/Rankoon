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

public sealed class LeaderboardRealtimePublisher(RankoonDbContext database, IServiceProvider services, IHubContext<LeaderboardHub> hub, LeaderboardSubscriptionRegistry subscriptions, TimeProvider timeProvider) : ILeaderboardRealtimePublisher
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

        await PublishEntryAsync(settings, userId, SeasonLeaderboardScope.Lifetime, null, cancellationToken);
        if (await database.GuildSeasons.Find(x => x.GuildId == guildId && x.Status == SeasonStatus.Active).AnyAsync(cancellationToken))
            await PublishEntryAsync(settings, userId, SeasonLeaderboardScope.CurrentSeason, null, cancellationToken);
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

    private async Task PublishEntryAsync(GuildLeaderboardSettings settings, ulong userId, SeasonLeaderboardScope scope, string? seasonId, CancellationToken cancellationToken)
    {
        await PublishEntryForAudienceAsync("members", true);
        await PublishEntryForAudienceAsync("public", false);

        async Task PublishEntryForAudienceAsync(string audience, bool isMember)
        {
            var page = await services.GetRequiredService<LeaderboardService>().GetScopedPageAsync(settings, isMember, userId, scope, seasonId, null, 10, true, cancellationToken);
            var entry = page.Items.FirstOrDefault(item => item.UserId == userId.ToString()) is { } item
                ? item with { IsCurrentUser = false }
                : null;
            var operation = entry == null ? "remove" : "upsert";
            await hub.Clients.Group(HubGroupNames.Leaderboard(audience, settings.Alias, scope, seasonId))
                .SendAsync("leaderboardEntryChanged", new LeaderboardEntryChanged(settings.Alias, scope, seasonId, operation, userId.ToString(), entry), cancellationToken);
        }
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
