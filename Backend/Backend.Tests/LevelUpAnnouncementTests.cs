using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class LevelUpAnnouncementTests
{
    [Fact]
    public void Recent_server_message_is_skipped_when_another_message_is_available()
    {
        var selector = new LevelUpTemplateSelector(new LevelUpTemplateRenderer(), new FirstAnnouncementRandom());
        var profile = new LevelAnnouncementProfile
        {
            LevelUp = new LevelAnnouncementSet
            {
                Groups =
                [
                    new LevelAnnouncementGroup { Id = "group-a", Name = "A", Messages = [new LevelAnnouncementMessage { Id = "message-a", Content = "A" }] },
                    new LevelAnnouncementGroup { Id = "group-b", Name = "B", Messages = [new LevelAnnouncementMessage { Id = "message-b", Content = "B" }] }
                ]
            }
        };

        var selection = selector.Select(profile, LevelAnnouncementKind.LevelUp, Context(), ["group-a"], ["message-a"]);

        Assert.NotNull(selection);
        Assert.Equal("message-b", selection.Message.Id);
    }

    private static LevelUpRenderContext Context() => new("<@1>", "User", "user", 1, 1, 2, 0, 100, 100, "message", "#general", "Guild", 1, 0, 0, 1, []);

    private sealed class FirstAnnouncementRandom : ILevelUpRandom
    {
        public int Next(int maximumExclusive) => 0;
    }
}
