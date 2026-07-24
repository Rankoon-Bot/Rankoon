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
            {
                var guildUser = context?.Guild.GetUser(userId);
                var user = guildUser ?? context?.Client.GetUser(userId);
                iconUrls[userId] = user == null ? CreateDefaultAvatarUrl(userId) : CreateAvatarUrl(guildId, user, guildUser);
            }
        }
        catch
        {
            // A cache race must not make the public leaderboard unavailable.
            foreach (var userId in ids)
                iconUrls[userId] = CreateDefaultAvatarUrl(userId);
        }

        return iconUrls;
    }

    private static string CreateAvatarUrl(ulong guildId, IUser user, SocketGuildUser? guildUser)
    {
        // This matches the authenticated-header avatar URL format, using hashes already held by Discord.Net.
        if (!string.IsNullOrEmpty(guildUser?.GuildAvatarId))
            return $"https://cdn.discordapp.com/guilds/{guildId}/users/{user.Id}/avatars/{guildUser.GuildAvatarId}.{Extension(guildUser.GuildAvatarId)}?size=128";
        if (!string.IsNullOrEmpty(user.AvatarId))
            return $"https://cdn.discordapp.com/avatars/{user.Id}/{user.AvatarId}.{Extension(user.AvatarId)}?size=128";
        return user.GetDefaultAvatarUrl();
    }

    internal static string CreateDefaultAvatarUrl(ulong userId) => $"https://cdn.discordapp.com/embed/avatars/{(userId >> 22) % 6}.png";
    private static string Extension(string avatarId) => avatarId.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "webp";
}
