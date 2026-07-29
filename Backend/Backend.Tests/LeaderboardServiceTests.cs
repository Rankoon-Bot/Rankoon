using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class LeaderboardServiceTests
{
    [Fact]
    public void UniqueUsers_keeps_only_the_first_entry_for_each_user()
    {
        var entries = new[]
        {
            Entry(1, "1"),
            Entry(2, "2"),
            Entry(3, "1"),
            Entry(4, "3"),
        };

        var result = LeaderboardService.UniqueUsers(entries);

        Assert.Collection(result,
            entry => Assert.Equal((1, "1"), (entry.Rank, entry.UserId)),
            entry => Assert.Equal((2, "2"), (entry.Rank, entry.UserId)),
            entry => Assert.Equal((4, "3"), (entry.Rank, entry.UserId)));
    }

    [Theory]
    [InlineData(50, 70, 50)]
    [InlineData(100, 70, 69)]
    [InlineData(10, 0, 0)]
    public void Window_offset_preserves_partial_final_windows(int requested, long total, int expected) =>
        Assert.Equal(expected, LeaderboardService.ClampWindowOffset(requested, total));

    [Theory]
    [InlineData(0, 200, 50, 0)]
    [InlineData(100, 200, 50, 84)]
    [InlineData(199, 200, 50, 150)]
    public void Center_offset_keeps_the_current_user_inside_the_window(long index, long total, int take, int expected) =>
        Assert.Equal(expected, LeaderboardService.CenterOffset(index, total, take));

    [Fact]
    public void Uncached_users_receive_a_discord_default_avatar_url() =>
        Assert.Equal("https://cdn.discordapp.com/embed/avatars/0.png", GuildUserPresentationService.CreateDefaultAvatarUrl(1528473110666412042));

    [Fact]
    public void Level_rewards_are_resolved_trimmed_and_sorted_without_exposing_role_ids()
    {
        var rewards = new[]
        {
            new LevelRole { Level = 20, RoleId = 3, Description = "  " },
            new LevelRole { Level = 10, RoleId = 2, Description = "  Community access  " },
            new LevelRole { Level = 10, RoleId = 1, Description = null },
            new LevelRole { Level = 5, RoleId = 999, Description = "Missing" },
        };
        var names = new Dictionary<ulong, string> { [1] = "Veteran", [2] = "Active member", [3] = "Champion" };

        var result = LeaderboardService.ResolveLevelRewards(rewards, id => names.GetValueOrDefault(id));

        Assert.Collection(result,
            reward => Assert.Equal((10, "Active member", "Community access"), (reward.Level, reward.RoleName, reward.Description)),
            reward => Assert.Equal((10, "Veteran", null), (reward.Level, reward.RoleName, reward.Description)),
            reward => Assert.Equal((20, "Champion", null), (reward.Level, reward.RoleName, reward.Description)));
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("roleId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("999", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Level_rewards_treat_missing_configuration_as_empty() =>
        Assert.Empty(LeaderboardService.ResolveLevelRewards(null, _ => throw new InvalidOperationException()));

    [Fact]
    public void Level_reward_descriptions_are_backward_compatible_with_existing_bson()
    {
        var legacy = new BsonDocument { ["Level"] = 10, ["RoleId"] = 42L };

        var reward = BsonSerializer.Deserialize<LevelRole>(legacy);

        Assert.Null(reward.Description);
        reward.Description = "Public reward";
        var saved = reward.ToBsonDocument();
        Assert.Equal("Public reward", saved[nameof(LevelRole.Description)].AsString);
    }

    private static LeaderboardEntryDto Entry(long rank, string userId) => new(rank, userId, userId, null, 0, 0, 0, 0, false);
}
