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

    private static LeaderboardEntryDto Entry(long rank, string userId) => new(rank, userId, userId, 0, 0, 0, 0, false);
}
