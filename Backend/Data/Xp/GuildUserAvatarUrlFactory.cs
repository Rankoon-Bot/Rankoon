namespace Rankoon.Data.Xp;

public interface IGuildUserAvatarUrlFactory
{
    string CreateUrl(ulong guildId, ulong userId, string? avatarId, string? guildAvatarId, byte? defaultAvatarIndex, ushort size = 128);
    string CreateDefaultAvatarUrl(ulong userId, byte? knownDefaultAvatarIndex = null);
}

public sealed class GuildUserAvatarUrlFactory : IGuildUserAvatarUrlFactory
{
    public string CreateUrl(ulong guildId, ulong userId, string? avatarId, string? guildAvatarId, byte? defaultAvatarIndex, ushort size = 128)
    {
        ValidateSize(size);
        if (!string.IsNullOrWhiteSpace(guildAvatarId)) return $"https://cdn.discordapp.com/guilds/{guildId}/users/{userId}/avatars/{guildAvatarId}.{Extension(guildAvatarId)}?size={size}";
        if (!string.IsNullOrWhiteSpace(avatarId)) return $"https://cdn.discordapp.com/avatars/{userId}/{avatarId}.{Extension(avatarId)}?size={size}";
        return CreateDefaultAvatarUrl(userId, defaultAvatarIndex);
    }

    public string CreateDefaultAvatarUrl(ulong userId, byte? knownDefaultAvatarIndex = null) =>
        $"https://cdn.discordapp.com/embed/avatars/{knownDefaultAvatarIndex ?? (byte)((userId >> 22) % 6)}.png";

    internal static void ValidateSize(ushort size)
    {
        if (size is < 16 or > 4096 || (size & (size - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(size), "Discord image size must be a power of two from 16 through 4096.");
    }

    private static string Extension(string avatarId) => avatarId.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "webp";
}
