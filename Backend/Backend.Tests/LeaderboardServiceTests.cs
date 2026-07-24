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

    private static LeaderboardEntryDto Entry(long rank, string userId) => new(rank, userId, userId, null, 0, 0, 0, 0, false);
}
