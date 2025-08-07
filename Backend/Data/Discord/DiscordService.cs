using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Rankoon.Data.Auth;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using System.Text;
using System.Text.Json;

namespace Rankoon.Data.Discord;

/// <summary>
/// Service for handling Discord OAuth operations
/// </summary>
public interface IDiscordService
{
    /// <summary>
    /// Get Discord OAuth authorization URL
    /// </summary>
    string GetAuthorizationUrl(string state);

    /// <summary>
    /// Exchange authorization code for access token
    /// </summary>
    Task<DiscordTokenResponse?> ExchangeCodeForTokenAsync(string code);

    /// <summary>
    /// Get user information from Discord API
    /// </summary>
    Task<DiscordUserInfo?> GetUserInfoAsync(string accessToken);

    /// <summary>
    /// Refresh Discord access token
    /// </summary>
    Task<DiscordTokenResponse?> RefreshTokenAsync(string refreshToken);

    /// <summary>
    /// Create or update user in database
    /// </summary>
    Task<DiscordUser> CreateOrUpdateUserAsync(DiscordUserInfo userInfo, DiscordTokenResponse tokenResponse);
}

public class DiscordService : IDiscordService
{
    private readonly DiscordSettings _discordSettings;
    private readonly RankoonDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly ILogger<DiscordService> _logger;

    private const string TokenEndpoint = "https://discord.com/api/oauth2/token";
    private const string UserEndpoint = "https://discord.com/api/users/@me";
    private const string AuthEndpoint = "https://discord.com/api/oauth2/authorize";

    public DiscordService(
        IOptions<DiscordSettings> discordSettings,
        RankoonDbContext dbContext,
        HttpClient httpClient,
        ILogger<DiscordService> logger)
    {
        _discordSettings = discordSettings.Value;
        _dbContext = dbContext;
        _httpClient = httpClient;
        _logger = logger;
    }

    public string GetAuthorizationUrl(string state)
    {
        var scopes = string.Join(" ", _discordSettings.Scopes);
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = _discordSettings.ClientId,
            ["redirect_uri"] = _discordSettings.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = scopes,
            ["state"] = state
        };

        var queryString = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return $"{AuthEndpoint}?{queryString}";
    }

    public async Task<DiscordTokenResponse?> ExchangeCodeForTokenAsync(string code)
    {
        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["client_id"] = _discordSettings.ClientId,
                ["client_secret"] = _discordSettings.ClientSecret,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = _discordSettings.RedirectUri
            };

            var content = new FormUrlEncodedContent(parameters);
            var response = await _httpClient.PostAsync(TokenEndpoint, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Discord token exchange failed: {StatusCode} - {Content}", response.StatusCode, errorContent);
                return null;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<DiscordTokenResponse>(jsonResponse, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exchanging Discord authorization code");
            return null;
        }
    }

    public async Task<DiscordUserInfo?> GetUserInfoAsync(string accessToken)
    {
        try
        {
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            
            var response = await _httpClient.GetAsync(UserEndpoint);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Discord user info request failed: {StatusCode} - {Content}", response.StatusCode, errorContent);
                return null;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<DiscordUserInfo>(jsonResponse, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Discord user info");
            return null;
        }
        finally
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;
        }
    }

    public async Task<DiscordTokenResponse?> RefreshTokenAsync(string refreshToken)
    {
        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["client_id"] = _discordSettings.ClientId,
                ["client_secret"] = _discordSettings.ClientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            };

            var content = new FormUrlEncodedContent(parameters);
            var response = await _httpClient.PostAsync(TokenEndpoint, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Discord token refresh failed: {StatusCode} - {Content}", response.StatusCode, errorContent);
                return null;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<DiscordTokenResponse>(jsonResponse, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing Discord token");
            return null;
        }
    }

    public async Task<DiscordUser> CreateOrUpdateUserAsync(DiscordUserInfo userInfo, DiscordTokenResponse tokenResponse)
    {
        var filter = Builders<DiscordUser>.Filter.Eq(u => u.DiscordId, userInfo.id);
        var existingUser = await _dbContext.DiscordUsers.Find(filter).FirstOrDefaultAsync();

        var expiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.expires_in);

        if (existingUser != null)
        {
            // Update existing user
            var update = Builders<DiscordUser>.Update
                .Set(u => u.Username, userInfo.username)
                .Set(u => u.Discriminator, userInfo.discriminator)
                .Set(u => u.DisplayName, userInfo.global_name)
                .Set(u => u.Email, userInfo.email)
                .Set(u => u.Avatar, userInfo.avatar)
                .Set(u => u.Verified, userInfo.verified ?? false)
                .Set(u => u.AccessToken, tokenResponse.access_token)
                .Set(u => u.RefreshToken, tokenResponse.refresh_token)
                .Set(u => u.TokenExpiresAt, expiresAt)
                .Set(u => u.UpdatedAt, DateTime.UtcNow)
                .Set(u => u.LastLogin, DateTime.UtcNow);

            await _dbContext.DiscordUsers.UpdateOneAsync(filter, update);
            
            // Fetch the updated user
            existingUser = await _dbContext.DiscordUsers.Find(filter).FirstOrDefaultAsync();
            return existingUser!;
        }
        else
        {
            // Create new user
            var newUser = new DiscordUser
            {
                DiscordId = userInfo.id,
                Username = userInfo.username,
                Discriminator = userInfo.discriminator,
                DisplayName = userInfo.global_name,
                Email = userInfo.email,
                Avatar = userInfo.avatar,
                Verified = userInfo.verified ?? false,
                AccessToken = tokenResponse.access_token,
                RefreshToken = tokenResponse.refresh_token,
                TokenExpiresAt = expiresAt,
                LastLogin = DateTime.UtcNow
            };

            await _dbContext.DiscordUsers.InsertOneAsync(newUser);
            return newUser;
        }
    }
}
