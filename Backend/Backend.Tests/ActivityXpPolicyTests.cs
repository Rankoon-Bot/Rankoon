using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class ActivityXpPolicyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 0)]
    [InlineData(10, 5)]
    [InlineData(55, 27)]
    [InlineData(100, 50)]
    [InlineData(1000, 50)]
    public void Message_points_enforce_minimum_length_and_cap(int length, int expected)
    {
        Assert.Equal(expected, ActivityXpPolicy.MessagePoints(length, new() { MinimumCharacters = 10, MaximumCharacters = 100 }));
    }

    [Fact]
    public void Message_points_are_monotonic_and_bounded()
    {
        var settings = new MessageXpSettings();
        var values = Enumerable.Range(1, 2000).Select(x => ActivityXpPolicy.MessagePoints(x, settings)).ToArray();
        Assert.All(values, value => Assert.InRange(value, settings.MinimumPoints, settings.MaximumPoints));
        Assert.All(values.Zip(values.Skip(1)), pair => Assert.True(pair.First <= pair.Second));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("role")]
    [InlineData("channel")]
    [InlineData("category")]
    [InlineData("parent")]
    public void Every_activity_source_honors_shared_exclusions(string exclusion)
    {
        var settings = new GuildXpSettings();
        switch (exclusion)
        {
            case "disabled": settings.Enabled = false; break;
            case "role": settings.ExcludedRoleIds.Add(2); break;
            case "channel": settings.ExcludedChannelIds.Add(3); break;
            case "category": settings.ExcludedCategoryIds.Add(4); break;
            case "parent": settings.ExcludedChannelIds.Add(5); break;
        }
        Assert.True(ActivityXpPolicy.IsExcluded(settings, [1, 2], 3, 4, 5));
        Assert.False(ActivityXpPolicy.IsExcluded(new(), [1, 2], 3, 4, 5));
    }

    [Fact]
    public void Unrelated_exclusions_do_not_block_an_award()
    {
        var settings = new GuildXpSettings { ExcludedChannelIds = [10], ExcludedCategoryIds = [11], ExcludedRoleIds = [12] };
        Assert.False(ActivityXpPolicy.IsExcluded(settings, [2], 3, 4, 5));
        Assert.False(ActivityXpPolicy.IsExcluded(settings, []));
        Assert.True(ActivityXpPolicy.IsExcluded(settings, [12]));
    }

    [Fact]
    public void Thread_rate_prefers_its_own_multiplier_then_parent_then_default()
    {
        var settings = new GuildXpSettings { ChannelMultipliers = [new() { ChannelId = 1, Multiplier = 2 }, new() { ChannelId = 2, Multiplier = 3 }] };
        Assert.Equal(2, ActivityXpPolicy.ChannelMultiplier(settings, 1, 2));
        Assert.Equal(3, ActivityXpPolicy.ChannelMultiplier(settings, 3, 2));
        Assert.Equal(1, ActivityXpPolicy.ChannelMultiplier(settings, 3));
    }

    [Fact]
    public void Zero_channel_multiplier_disables_an_award_without_falling_back_to_parent()
    {
        var settings = new GuildXpSettings { ChannelMultipliers = [new() { ChannelId = 1, Multiplier = 0 }, new() { ChannelId = 2, Multiplier = 3 }] };
        Assert.Equal(0, ActivityXpPolicy.ChannelMultiplier(settings, 1, 2));
    }

    [Theory]
    [InlineData("message")]
    [InlineData("reaction")]
    [InlineData("thread_create")]
    [InlineData("thread_message")]
    [InlineData("event_interest")]
    [InlineData("voice")]
    public void Channel_and_booster_multipliers_compose_for_all_sources(string source)
    {
        var settings = new GuildXpSettings { ServerBooster = new() { Enabled = true, Tiers = [new() { Multiplier = 1.5m }] } };
        var now = DateTimeOffset.UtcNow;
        var award = new ServerBoosterXpMultiplierResolver(TimeProvider.System).Apply(source, 10, 2, settings, now.AddMonths(-1), now);
        Assert.Equal(30, award.Amount);
    }
}
