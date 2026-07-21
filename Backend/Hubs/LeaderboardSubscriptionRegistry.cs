using System.Collections.Concurrent;
using Rankoon.Data.Model;

namespace Rankoon.Hubs;

public sealed record LeaderboardSubscription(string ConnectionId, ulong GuildId, ulong? UserId, string Audience, string Group, string AudienceGroup, SeasonLeaderboardScope Scope, string? SeasonId);

public sealed class LeaderboardSubscriptionRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, LeaderboardSubscription>> connections = new();

    public bool Add(LeaderboardSubscription subscription) => connections.GetOrAdd(subscription.ConnectionId, _ => []).TryAdd(subscription.Group, subscription);

    public LeaderboardSubscription? Remove(string connectionId, string group)
    {
        if (!connections.TryGetValue(connectionId, out var subscriptions)) return null;
        subscriptions.TryRemove(group, out var removed);
        if (subscriptions.IsEmpty) connections.TryRemove(connectionId, out _);
        return removed;
    }

    public bool HasAudience(string connectionId, string audienceGroup) =>
        connections.TryGetValue(connectionId, out var subscriptions) && subscriptions.Values.Any(x => x.AudienceGroup == audienceGroup);

    public IReadOnlyList<LeaderboardSubscription> GetGuildSubscriptions(ulong guildId) =>
        connections.Values.SelectMany(x => x.Values).Where(x => x.GuildId == guildId).DistinctBy(x => x.Group).ToList();

    public IReadOnlyList<LeaderboardSubscription> RemoveMemberSubscriptions(ulong guildId, ulong userId)
    {
        var removed = new List<LeaderboardSubscription>();
        foreach (var pair in connections)
        {
            foreach (var subscription in pair.Value.Values.Where(x => x.GuildId == guildId && x.UserId == userId && x.Audience == "members").ToArray())
                if (pair.Value.TryRemove(subscription.Group, out _)) removed.Add(subscription);
            if (pair.Value.IsEmpty) connections.TryRemove(pair.Key, out _);
        }
        return removed;
    }

    public void RemoveConnection(string connectionId) => connections.TryRemove(connectionId, out _);
}
