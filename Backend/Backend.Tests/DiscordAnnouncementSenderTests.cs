using Discord;
using Rankoon.Data.Discord;
using Xunit;

namespace Rankoon.Tests;

public sealed class DiscordAnnouncementSenderTests
{
    [Fact]
    public void Allows_only_the_transition_user_without_the_users_flag()
    {
        var mentions = DiscordAnnouncementSender.CreateAllowedMentions(123UL, true);

        Assert.Null(mentions.AllowedTypes);
        Assert.Single(mentions.UserIds);
        Assert.Contains(123UL, mentions.UserIds);
    }

    [Fact]
    public void Disables_mentions_when_user_notifications_are_off()
    {
        var mentions = DiscordAnnouncementSender.CreateAllowedMentions(123UL, false);

        Assert.Null(mentions.AllowedTypes);
        Assert.Empty(mentions.UserIds);
    }
}
