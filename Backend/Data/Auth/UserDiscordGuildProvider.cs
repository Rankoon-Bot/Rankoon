using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Utils;
using Rankoon.Data.Discord;

namespace Rankoon.Data.Auth;

/// <summary>Uses the user's OAuth grant when no bot gateway can provide guild metadata.</summary>
public interface IUserDiscordGuildProvider
{
    Task<IReadOnlyList<DiscordGuildInfo>> GetGuildsAsync(ulong discordUserId, bool refresh = false, CancellationToken cancellationToken = default);
    Task<bool> IsGuildOwnerAsync(ulong discordUserId, ulong guildId, CancellationToken cancellationToken = default);
    Task<bool> IsGuildMemberAsync(ulong discordUserId, ulong guildId, CancellationToken cancellationToken = default);
}

public sealed class UserDiscordGuildProvider(RankoonDbContext database, IDiscordService discord) : IUserDiscordGuildProvider
{
    public async Task<IReadOnlyList<DiscordGuildInfo>> GetGuildsAsync(ulong discordUserId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var user = await database.DiscordUsers.Find(x => x.DiscordId == discordUserId.ToString()).FirstOrDefaultAsync(cancellationToken);
        if (user?.AccessToken is not { Length: > 0 }) return [];
        if (user.TokenExpiresAt <= DateTime.UtcNow && user.RefreshToken is { Length: > 0 })
        {
            var refreshed = await discord.RefreshTokenAsync(user.RefreshToken);
            if (refreshed == null) return [];
            user.AccessToken = refreshed.access_token;
            user.RefreshToken = refreshed.refresh_token;
            user.TokenExpiresAt = DateTime.UtcNow.AddSeconds(refreshed.expires_in);
            await database.DiscordUsers.ReplaceOneAsync(x => x.Id == user.Id, user, cancellationToken: cancellationToken);
        }
        var key = $"discord_user_guilds_{discordUserId}_{(refresh ? "refresh" : "cached")}";
        return await CacheManager.GetOrSetAsync<IReadOnlyList<DiscordGuildInfo>>(
            key,
            async () => await discord.GetUserGuildsAsync(user.AccessToken) ?? [],
            DateTimeOffset.UtcNow.Add(refresh ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(1)));
    }

    public async Task<bool> IsGuildOwnerAsync(ulong discordUserId, ulong guildId, CancellationToken cancellationToken = default) =>
        (await GetGuildsAsync(discordUserId, cancellationToken: cancellationToken)).Any(guild => guild.owner && guild.id == guildId.ToString());

    public async Task<bool> IsGuildMemberAsync(ulong discordUserId, ulong guildId, CancellationToken cancellationToken = default) =>
        (await GetGuildsAsync(discordUserId, cancellationToken: cancellationToken)).Any(guild => guild.id == guildId.ToString());
}
