using Discord;
using Discord.WebSocket;
using Rankoon.Data.Discord;

namespace Rankoon.Data.Xp;

public interface IGuildUserPresentationService
{
    IReadOnlyDictionary<ulong, string?> ResolveIconUrls(ulong guildId, IEnumerable<ulong> userIds);
}

// Deliberately reads only Discord.Net's socket cache; leaderboard rendering must not create REST fan-out.
public sealed class GuildUserPresentationService(IGuildDiscordContextResolver discord) : IGuildUserPresentationService
{
    public IReadOnlyDictionary<ulong, string?> ResolveIconUrls(ulong guildId, IEnumerable<ulong> userIds)
    {
        var ids = userIds.Distinct().ToArray();
        var iconUrls = ids.ToDictionary(id => id, _ => (string?)null);
        try
        {
            var context = discord.ResolveAsync(guildId).GetAwaiter().GetResult();
            foreach (var userId in ids)
                iconUrls[userId] = context?.Guild.GetUser(userId) is { } user
                    ? CreateAvatarUrl(user)
                    : CreateDefaultAvatarUrl(userId);
        }
        catch
        {
            // A cache race must not make the public leaderboard unavailable.
            foreach (var userId in ids)
                iconUrls[userId] = CreateDefaultAvatarUrl(userId);
        }

        return iconUrls;
    }

    private static string CreateAvatarUrl(SocketGuildUser user)
    {
        // This matches the authenticated-header avatar URL format, using hashes already held by Discord.Net.
        if (!string.IsNullOrEmpty(user.DisplayAvatarId))
            return $"https://cdn.discordapp.com/avatars/{user.Id}/{user.DisplayAvatarId}.{Extension(user.DisplayAvatarId)}?size=128";
        return user.GetDefaultAvatarUrl();
    }

    internal static string CreateDefaultAvatarUrl(ulong userId) => $"https://cdn.discordapp.com/embed/avatars/{(userId >> 22) % 6}.png";
    private static string Extension(string avatarId) => avatarId.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "webp";
}
