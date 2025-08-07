using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Rankoon.Data.Auth;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Utils;

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
    Task<TokenResponse?> HandleCallbackAsync(string code, string? state);

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
}

public class AuthService : IAuthService
{
    private readonly IDiscordService _discordService;
    private readonly IJwtService _jwtService;
    private readonly RankoonDbContext _dbContext;
    private readonly JwtSettings _jwtSettings;
    private readonly FrontendSettings _frontendSettings;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IDiscordService discordService,
        IJwtService jwtService,
        RankoonDbContext dbContext,
        IOptions<JwtSettings> jwtSettings,
        IOptions<FrontendSettings> frontendSettings,
        ILogger<AuthService> logger)
    {
        _discordService = discordService;
        _jwtService = jwtService;
        _dbContext = dbContext;
        _jwtSettings = jwtSettings.Value;
        _frontendSettings = frontendSettings.Value;
        _logger = logger;
    }

    public string GetLoginUrl(string? returnUrl = null)
    {
        // Generate a random state parameter for CSRF protection
        var state = Guid.NewGuid().ToString();
        
        // Optionally encode the return URL in the state
        if (!string.IsNullOrEmpty(returnUrl))
        {
            state += $"|{returnUrl}";
        }

        return _discordService.GetAuthorizationUrl(state);
    }

    public async Task<TokenResponse?> HandleCallbackAsync(string code, string? state)
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
                Token = refreshToken,
                UserId = user.Id!,
                ExpiresAt = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays)
            };

            await _dbContext.RefreshTokens.InsertOneAsync(refreshTokenEntity);

            return new TokenResponse
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
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
            _logger.LogError(ex, "Error handling Discord OAuth callback");
            return null;
        }
    }

    public async Task<TokenResponse?> RefreshTokenAsync(string refreshToken, string? ipAddress = null)
    {
        try
        {
            // Find refresh token in database
            var tokenFilter = Builders<RefreshToken>.Filter.And(
                Builders<RefreshToken>.Filter.Eq(t => t.Token, refreshToken),
                Builders<RefreshToken>.Filter.Eq(t => t.Revoked, false),
                Builders<RefreshToken>.Filter.Gt(t => t.ExpiresAt, DateTime.UtcNow)
            );

            var storedToken = await _dbContext.RefreshTokens.Find(tokenFilter).FirstOrDefaultAsync();
            if (storedToken == null)
            {
                _logger.LogWarning("Invalid or expired refresh token");
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

            // Update old refresh token as used
            var updateOld = Builders<RefreshToken>.Update
                .Set(t => t.LastUsedAt, DateTime.UtcNow)
                .Set(t => t.LastUsedIp, ipAddress);

            await _dbContext.RefreshTokens.UpdateOneAsync(
                Builders<RefreshToken>.Filter.Eq(t => t.Id, storedToken.Id),
                updateOld
            );

            // Create new refresh token
            var newRefreshTokenEntity = new RefreshToken
            {
                Token = newRefreshToken,
                UserId = user.Id!,
                ExpiresAt = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
                IssuedIp = ipAddress
            };

            await _dbContext.RefreshTokens.InsertOneAsync(newRefreshTokenEntity);

            return new TokenResponse
            {
                AccessToken = newAccessToken,
                RefreshToken = newRefreshToken,
                ExpiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
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
            var filter = Builders<RefreshToken>.Filter.Eq(t => t.Token, refreshToken);
            var update = Builders<RefreshToken>.Update
                .Set(t => t.Revoked, true)
                .Set(t => t.RevokedAt, DateTime.UtcNow)
                .Set(t => t.RevokedReason, "User logout");

            var result = await _dbContext.RefreshTokens.UpdateOneAsync(filter, update);
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
}
