using Rankoon.Data.Model;
using Rankoon.Hubs;
using Xunit;

namespace Backend.Tests;

public sealed class LeaderboardSubscriptionRegistryTests
{
    [Fact]
    public void Add_deduplicates_a_connection_scope_subscription()
    {
        var registry = new LeaderboardSubscriptionRegistry();
        var subscription = Subscription("connection", "group", "audience", SeasonLeaderboardScope.Lifetime);

        Assert.True(registry.Add(subscription));
        Assert.False(registry.Add(subscription));
        Assert.Single(registry.GetGuildSubscriptions(1));
    }

    [Fact]
    public void Removing_one_scope_keeps_the_shared_audience_subscription()
    {
        var registry = new LeaderboardSubscriptionRegistry();
        registry.Add(Subscription("connection", "lifetime", "audience", SeasonLeaderboardScope.Lifetime));
        registry.Add(Subscription("connection", "current", "audience", SeasonLeaderboardScope.CurrentSeason));

        var removed = registry.Remove("connection", "lifetime");

        Assert.NotNull(removed);
        Assert.True(registry.HasAudience("connection", "audience"));
        Assert.Single(registry.GetGuildSubscriptions(1));
    }

    [Fact]
    public void RemoveMemberSubscriptions_removes_only_the_member_audience()
    {
        var registry = new LeaderboardSubscriptionRegistry();
        registry.Add(Subscription("member", "member-group", "member-audience", SeasonLeaderboardScope.Lifetime, userId: 2, audience: "members"));
        registry.Add(Subscription("public", "public-group", "public-audience", SeasonLeaderboardScope.Lifetime));

        var removed = registry.RemoveMemberSubscriptions(1, 2);

        Assert.Single(removed);
        Assert.False(registry.HasAudience("member", "member-audience"));
        Assert.Single(registry.GetGuildSubscriptions(1));
    }

    private static LeaderboardSubscription Subscription(string connectionId, string group, string audienceGroup, SeasonLeaderboardScope scope, ulong? userId = null, string audience = "public") =>
        new(connectionId, 1, userId, audience, group, audienceGroup, scope, null);
}
