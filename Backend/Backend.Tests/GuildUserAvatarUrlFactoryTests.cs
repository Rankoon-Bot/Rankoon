using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class GuildUserAvatarUrlFactoryTests
{
    private readonly GuildUserAvatarUrlFactory factory = new();

    [Fact]
    public void Guild_avatar_takes_precedence_over_global_avatar() =>
        Assert.Equal("https://cdn.discordapp.com/guilds/10/users/20/avatars/guild.webp?size=128", factory.CreateUrl(10, 20, "global", "guild", null));

    [Fact]
    public void Animated_global_avatar_uses_gif() =>
        Assert.Equal("https://cdn.discordapp.com/avatars/20/a_global.gif?size=128", factory.CreateUrl(10, 20, "a_global", null, null));

    [Fact]
    public void Known_default_avatar_index_is_used() =>
        Assert.Equal("https://cdn.discordapp.com/embed/avatars/4.png", factory.CreateUrl(10, 20, null, null, 4));

    [Fact]
    public void Unknown_default_avatar_index_uses_snowflake_fallback()
    {
        const ulong userId = 1528473110666412042;
        Assert.Equal($"https://cdn.discordapp.com/embed/avatars/{(userId >> 22) % 6}.png", factory.CreateDefaultAvatarUrl(userId));
    }

    [Theory]
    [InlineData((ushort)15)]
    [InlineData((ushort)17)]
    [InlineData((ushort)8192)]
    public void Invalid_image_size_is_rejected(ushort size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.CreateUrl(1, 2, "avatar", null, null, size));
}
