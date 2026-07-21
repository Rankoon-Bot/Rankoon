using Discord.WebSocket;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Rankoon.Data.Auth;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Utils;
using System.Security.Cryptography;
using System.Text;

namespace Rankoon.Data.Auth;

/// <summary>
/// Main authentication service
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Generate login URL for Discord OAuth
    /// </summary>
    string GetLoginUrl(string? returnUrl = null);

    /// <summary>
    /// Handle Discord OAuth callback
    /// </summary>
    Task<TokenResponse?> HandleCallbackAsync(string code);

    /// <summary>
    /// Refresh JWT tokens using refresh token
    /// </summary>
    Task<TokenResponse?> RefreshTokenAsync(string refreshToken, string? ipAddress = null);

    /// <summary>
    /// Revoke refresh token
    /// </summary>
    Task<bool> RevokeTokenAsync(string refreshToken);

    /// <summary>
    /// Get user by ID
    /// </summary>
    Task<DiscordUser?> GetUserAsync(string userId);

    /// <summary>
    /// Get user's Discord guilds
    /// </summary>
    Task<GuildDto[]?> GetUserGuildsAsync(string userId);
}

public class AuthService : IAuthService
{
    private const long AdministratorPermission = 1L << 3;
    private const long ManageGuildPermission = 1L << 5;

    private readonly DiscordShardedClient discord;
    private readonly IDiscordService _discordService;
    private readonly IJwtService _jwtService;
    private readonly RankoonDbContext _dbContext;
    private readonly DiscordSettings _discordSettings;
    private readonly JwtSettings _jwtSettings;
    private readonly FrontendSettings _frontendSettings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        DiscordShardedClient discord,
        IDiscordService discordService,
        IJwtService jwtService,
        RankoonDbContext dbContext,
        IOptions<DiscordSettings> discordSettings,
        IOptions<JwtSettings> jwtSettings,
        IOptions<FrontendSettings> frontendSettings,
        TimeProvider timeProvider,
        ILogger<AuthService> logger)
    {
        this.discord = discord;
        _discordService = discordService;
        _jwtService = jwtService;
        _dbContext = dbContext;
        _discordSettings = discordSettings.Value;
        _jwtSettings = jwtSettings.Value;
        _frontendSettings = frontendSettings.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string GetLoginUrl(string? returnUrl = null)
    {
        // Keep the OAuth state opaque; its associated return route is retained server-side.
        var state = Guid.NewGuid().ToString();
        CacheManager.GetOrSetAsync<string>(
            $"auth_state_{state}",
            () => Task.FromResult(state),
            _timeProvider.GetUtcNow().AddMinutes(5)
        ).Wait();

        if (!string.IsNullOrEmpty(returnUrl))
        {
            CacheManager.GetOrSetAsync<string>(
                $"auth_return_{state}",
                () => Task.FromResult(returnUrl),
                _timeProvider.GetUtcNow().AddMinutes(5)
            ).Wait();
        }

        return _discordService.GetAuthorizationUrl(state);
    }

    public async Task<TokenResponse?> HandleCallbackAsync(string code)
    {
        try
        {


            // Exchange authorization code for Discord access token
            var tokenResponse = await _discordService.ExchangeCodeForTokenAsync(code);
            if (tokenResponse == null)
            {
                _logger.LogError("Failed to exchange Discord authorization code");
                return null;
            }

            // Get user information from Discord
            var userInfo = await _discordService.GetUserInfoAsync(tokenResponse.access_token);
            if (userInfo == null)
            {
                _logger.LogError("Failed to get Discord user information");
                return null;
            }

            // Create or update user in our database
            var user = await _discordService.CreateOrUpdateUserAsync(userInfo, tokenResponse);
            
            // Generate our own JWT tokens
            var accessToken = _jwtService.GenerateAccessToken(user);
            var refreshToken = _jwtService.GenerateRefreshToken();

            // Store refresh token in database
            var refreshTokenEntity = new RefreshToken
            {
                TokenHash = HashRefreshToken(refreshToken),
                UserId = user.Id!,
                FamilyId = Guid.NewGuid().ToString("N"),
                ExpiresAt = _timeProvider.GetUtcNow().UtcDateTime.AddDays(_jwtSettings.RefreshTokenExpirationDays)
            };

            await _dbContext.RefreshTokens.InsertOneAsync(refreshTokenEntity);

            return new TokenResponse
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
                User = new DiscordUserDto
                {
                    Id = user.Id!,
                    DiscordId = user.DiscordId,
                    Username = user.Username,
                    DisplayName = user.DisplayName,
                    Email = user.Email,
                    Avatar = user.Avatar,
                    Verified = user.Verified
                }
            };
        }
        catch (Exception)
        {
            _logger.LogError("Error handling Discord OAuth callback");
            return null;
        }
    }

    public async Task<TokenResponse?> RefreshTokenAsync(string refreshToken, string? ipAddress = null)
    {
        try
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var tokenHash = HashRefreshToken(refreshToken);
            var tokenIdentityFilter = TokenIdentityFilter(refreshToken, tokenHash);
            var candidate = await _dbContext.RefreshTokens.Find(tokenIdentityFilter).FirstOrDefaultAsync();
            if (candidate == null)
            {
                return null;
            }

            var familyId = candidate.FamilyId ?? Guid.NewGuid().ToString("N");
            var consumeFilter = Builders<RefreshToken>.Filter.And(
                Builders<RefreshToken>.Filter.Eq(t => t.Id, candidate.Id),
                Builders<RefreshToken>.Filter.Eq(t => t.Revoked, false),
                Builders<RefreshToken>.Filter.Gt(t => t.ExpiresAt, now),
                tokenIdentityFilter);
            var consumeUpdate = Builders<RefreshToken>.Update
                .Set(t => t.TokenHash, tokenHash)
                .Set(t => t.Token, string.Empty)
                .Set(t => t.FamilyId, familyId)
                .Set(t => t.Revoked, true)
                .Set(t => t.RevokedAt, now)
                .Set(t => t.RevokedReason, "Rotated")
                .Set(t => t.LastUsedAt, now)
                .Set(t => t.LastUsedIp, ipAddress);
            var storedToken = await _dbContext.RefreshTokens.FindOneAndUpdateAsync(
                consumeFilter,
                consumeUpdate,
                new FindOneAndUpdateOptions<RefreshToken> { ReturnDocument = ReturnDocument.Before });
            if (storedToken == null)
            {
                // A matching token that was already consumed is a replay attempt.
                var replayedToken = await _dbContext.RefreshTokens.Find(tokenIdentityFilter).FirstOrDefaultAsync();
                if (replayedToken is { Revoked: true, FamilyId: not null })
                {
                    await MarkReplayAndRevokeFamilyAsync(replayedToken, now);
                }

                return null;
            }

            // Get user
            var user = await GetUserAsync(storedToken.UserId);
            if (user == null)
            {
                _logger.LogError("User not found for refresh token");
                return null;
            }

            // Generate new tokens
            var newAccessToken = _jwtService.GenerateAccessToken(user);
            var newRefreshToken = _jwtService.GenerateRefreshToken();

            var newRefreshTokenEntity = new RefreshToken
            {
                TokenHash = HashRefreshToken(newRefreshToken),
                UserId = user.Id!,
                FamilyId = familyId,
                ExpiresAt = now.AddDays(_jwtSettings.RefreshTokenExpirationDays),
                IssuedIp = ipAddress
            };

            await _dbContext.RefreshTokens.InsertOneAsync(newRefreshTokenEntity);

            // A replay can race with insertion on standalone MongoDB. The replay marker makes
            // the family invalid even when it was observed before this child existed.
            if (await IsFamilyReplayDetectedAsync(storedToken.Id))
            {
                await RevokeFamilyAsync(familyId, "Refresh token replay detected", now);
                return null;
            }

            return new TokenResponse
            {
                AccessToken = newAccessToken,
                RefreshToken = newRefreshToken,
                ExpiresAt = now.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
                User = new DiscordUserDto
                {
                    Id = user.Id!,
                    DiscordId = user.DiscordId,
                    Username = user.Username,
                    DisplayName = user.DisplayName,
                    Email = user.Email,
                    Avatar = user.Avatar,
                    Verified = user.Verified
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing token");
            return null;
        }
    }

    public async Task<bool> RevokeTokenAsync(string refreshToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var tokenHash = HashRefreshToken(refreshToken);
            var tokenIdentityFilter = TokenIdentityFilter(refreshToken, tokenHash);
            var storedToken = await _dbContext.RefreshTokens.Find(tokenIdentityFilter).FirstOrDefaultAsync();
            if (storedToken == null) return false;

            if (!string.IsNullOrEmpty(storedToken.FamilyId))
            {
                return await RevokeFamilyAsync(storedToken.FamilyId, "User logout", now);
            }

            var update = Builders<RefreshToken>.Update
                .Set(t => t.TokenHash, tokenHash)
                .Set(t => t.Token, string.Empty)
                .Set(t => t.Revoked, true)
                .Set(t => t.RevokedAt, now)
                .Set(t => t.RevokedReason, "User logout");

            var result = await _dbContext.RefreshTokens.UpdateOneAsync(tokenIdentityFilter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking refresh token");
            return false;
        }
    }

    public async Task<DiscordUser?> GetUserAsync(string userId)
    {
        try
        {
            var filter = Builders<DiscordUser>.Filter.Eq(u => u.Id, userId);
            return await _dbContext.DiscordUsers.Find(filter).FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user by ID: {UserId}", userId);
            return null;
        }
    }

    public async Task<GuildDto[]?> GetUserGuildsAsync(string userId)
    {
        try
        {
            // Get user from database
            var user = await GetUserAsync(userId);
            if (user == null)
            {
                _logger.LogError("User not found: {UserId}", userId);
                return null;
            }

            // Check if Discord token is still valid
            if (user.TokenExpiresAt <= DateTime.UtcNow)
            {
                _logger.LogWarning("Discord token expired for user: {UserId}", userId);
                
                // Try to refresh the Discord token
                if (!string.IsNullOrEmpty(user.RefreshToken))
                {
                    var refreshedToken = await _discordService.RefreshTokenAsync(user.RefreshToken);
                    if (refreshedToken != null)
                    {
                        // Update user with new token
                        var filter = Builders<DiscordUser>.Filter.Eq(u => u.Id, userId);
                        var update = Builders<DiscordUser>.Update
                            .Set(u => u.AccessToken, refreshedToken.access_token)
                            .Set(u => u.RefreshToken, refreshedToken.refresh_token)
                            .Set(u => u.TokenExpiresAt, DateTime.UtcNow.AddSeconds(refreshedToken.expires_in))
                            .Set(u => u.UpdatedAt, DateTime.UtcNow);

                        await _dbContext.DiscordUsers.UpdateOneAsync(filter, update);
                        user.AccessToken = refreshedToken.access_token;
                    }
                    else
                    {
                        _logger.LogError("Failed to refresh Discord token for user: {UserId}", userId);
                        return null;
                    }
                }
                else
                {
                    _logger.LogError("No refresh token available for user: {UserId}", userId);
                    return null;
                }
            }

            // Get guilds from Discord API
            if (string.IsNullOrEmpty(user.AccessToken))
            {
                _logger.LogError("No access token available for user: {UserId}", userId);
                return null;
            }

            var discordGuilds = await CacheManager.GetOrSetAsync<DiscordGuildInfo[]?>(
                $"discord_user_guilds_{userId}",
                () => _discordService.GetUserGuildsAsync(user.AccessToken),
                DateTimeOffset.UtcNow.AddMinutes(1));
            if (discordGuilds == null)
            {
                _logger.LogError("Failed to fetch guilds from Discord for user: {UserId}", userId);
                return null;
            }

            var guildDtos = discordGuilds
                .Where(g => g.owner
                    || (g.permissions & (AdministratorPermission | ManageGuildPermission)) != 0
                    || (ulong.TryParse(g.id, out var guildId) && discord.GetGuild(guildId) != null))
                .Select(g =>
                {
                    var botInstalled = ulong.TryParse(g.id, out var guildId) && discord.GetGuild(guildId) != null;
                    return new GuildDto
                    {
                        Id = g.id,
                        Name = g.name,
                        Icon = g.icon,
                        Owner = g.owner,
                        Permissions = g.permissions.ToString(),
                        Features = g.features,
                        BotInstalled = botInstalled,
                        InviteUrl = GetBotInviteUrl(g.id)
                    };
                }).ToArray();

            return guildDtos;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user guilds for user: {UserId}", userId);
            return null;
        }
    }

    private string GetBotInviteUrl(string guildId)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = _discordSettings.ClientId,
            ["scope"] = "bot applications.commands",
            ["permissions"] = _discordSettings.BotInvitePermissions,
            ["guild_id"] = guildId,
            ["disable_guild_select"] = "true",
            ["integration_type"] = "0"
        };
        var queryString = string.Join("&", parameters.Select(parameter => $"{parameter.Key}={Uri.EscapeDataString(parameter.Value)}"));
        return $"https://discord.com/oauth2/authorize?{queryString}";
    }

    private static string HashRefreshToken(string refreshToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));

    private static FilterDefinition<RefreshToken> TokenIdentityFilter(string refreshToken, string tokenHash) =>
        Builders<RefreshToken>.Filter.Or(
            Builders<RefreshToken>.Filter.Eq(t => t.TokenHash, tokenHash),
            Builders<RefreshToken>.Filter.Eq(t => t.Token, refreshToken));

    private async Task<bool> RevokeFamilyAsync(string familyId, string reason, DateTime now)
    {
        var filter = Builders<RefreshToken>.Filter.And(
            Builders<RefreshToken>.Filter.Eq(t => t.FamilyId, familyId),
            Builders<RefreshToken>.Filter.Eq(t => t.Revoked, false));
        var update = Builders<RefreshToken>.Update
            .Set(t => t.Revoked, true)
            .Set(t => t.RevokedAt, now)
            .Set(t => t.RevokedReason, reason);
        var result = await _dbContext.RefreshTokens.UpdateManyAsync(filter, update);
        return result.ModifiedCount > 0;
    }

    private async Task MarkReplayAndRevokeFamilyAsync(RefreshToken token, DateTime now)
    {
        var filter = Builders<RefreshToken>.Filter.And(
            Builders<RefreshToken>.Filter.Eq(t => t.Id, token.Id),
            Builders<RefreshToken>.Filter.Eq(t => t.RevokedReason, "Rotated"));
        await _dbContext.RefreshTokens.UpdateOneAsync(filter,
            Builders<RefreshToken>.Update.Set(t => t.RevokedReason, "Refresh token replay detected"));
        await RevokeFamilyAsync(token.FamilyId!, "Refresh token replay detected", now);
    }

    private async Task<bool> IsFamilyReplayDetectedAsync(string? tokenId)
    {
        if (string.IsNullOrEmpty(tokenId)) return false;
        var filter = Builders<RefreshToken>.Filter.And(
            Builders<RefreshToken>.Filter.Eq(t => t.Id, tokenId),
            Builders<RefreshToken>.Filter.Eq(t => t.RevokedReason, "Refresh token replay detected"));
        return await _dbContext.RefreshTokens.Find(filter).AnyAsync();
    }
}
