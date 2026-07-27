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

public sealed class UserDiscordGuildProvider(RankoonDbContext database, IDiscordService discord, IDiscordOAuthTokenProtector tokens, TimeProvider timeProvider, IApplicationCache cache) : IUserDiscordGuildProvider
{
    public async Task<IReadOnlyList<DiscordGuildInfo>> GetGuildsAsync(ulong discordUserId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var user = await database.DiscordUsers.Find(x => x.DiscordId == discordUserId.ToString()).FirstOrDefaultAsync(cancellationToken);
        if (user is null) return [];
        var accessToken = tokens.ReadAccessToken(user);
        if (string.IsNullOrEmpty(accessToken)) return [];
        if (user.TokenExpiresAt <= timeProvider.GetUtcNow().UtcDateTime && tokens.ReadRefreshToken(user) is { Length: > 0 } refreshToken)
        {
            var refreshed = await discord.RefreshTokenAsync(refreshToken);
            if (refreshed == null) return [];
            accessToken = refreshed.access_token;
            var update = Builders<DiscordUser>.Update
                .Set(x => x.ProtectedAccessToken, tokens.ProtectAccessToken(accessToken))
                .Set(x => x.OAuthTokenProtectionVersion, DiscordOAuthTokenProtector.CurrentVersion)
                .Set(x => x.TokenExpiresAt, timeProvider.GetUtcNow().UtcDateTime.AddSeconds(refreshed.expires_in));
            if (!string.IsNullOrEmpty(refreshed.refresh_token)) update = update.Set(x => x.ProtectedRefreshToken, tokens.ProtectRefreshToken(refreshed.refresh_token));
            await database.DiscordUsers.UpdateOneAsync(x => x.Id == user.Id, update, cancellationToken: cancellationToken);
        }
        var key = $"discord_user_guilds_{discordUserId}_{(refresh ? "refresh" : "cached")}";
        return await cache.GetOrCreateAsync<IReadOnlyList<DiscordGuildInfo>>(
            key,
            async _ => await discord.GetUserGuildsAsync(accessToken) ?? [],
            timeProvider.GetUtcNow().Add(refresh ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(1)));
    }

    public async Task<bool> IsGuildOwnerAsync(ulong discordUserId, ulong guildId, CancellationToken cancellationToken = default) =>
        (await GetGuildsAsync(discordUserId, cancellationToken: cancellationToken)).Any(guild => guild.owner && guild.id == guildId.ToString());

    public async Task<bool> IsGuildMemberAsync(ulong discordUserId, ulong guildId, CancellationToken cancellationToken = default) =>
        (await GetGuildsAsync(discordUserId, cancellationToken: cancellationToken)).Any(guild => guild.id == guildId.ToString());
}
