using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Rankoon.Data.Auth;
using Rankoon.Data.Utils;
using Rankoon.Api;
using System.IdentityModel.Tokens.Jwt;

namespace Rankoon.Controllers;

/// <summary>
/// Controller for handling authentication operations
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ConsumedOAuthStates = new();
    private readonly IAuthService _authService;
    private readonly IAuthCookieService? _authCookies;
    private readonly FrontendSettings _frontendSettings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuthController> _logger;
    private readonly IBotOperatorAccessService _botOperatorAccess;
    private readonly IOAuthCallbackCookieService? _callbackCookieService;

    public AuthController(
        IAuthService authService,
        IOptions<FrontendSettings> frontendSettings,
        TimeProvider timeProvider,
        ILogger<AuthController> logger,
        IBotOperatorAccessService botOperatorAccess,
        IOAuthCallbackCookieService? callbackCookieService = null,
        IAuthCookieService? authCookies = null)
    {
        _authService = authService;
        _authCookies = authCookies;
        _frontendSettings = frontendSettings.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _botOperatorAccess = botOperatorAccess;
        _callbackCookieService = callbackCookieService;
    }

    /// <summary>
    /// Get Discord OAuth login URL
    /// </summary>
    /// <param name="returnUrl">Optional return URL after successful authentication</param>
    /// <returns>Login URL for Discord OAuth</returns>
    [HttpGet("login")]
    [EnableRateLimiting(RateLimitPolicies.OAuthLogin)]
    public IActionResult GetLoginUrl([FromQuery] string? returnUrl = null)
    {
        try
        {
            var loginUrl = _authService.GetLoginUrl(IsSafeReturnUrl(returnUrl) ? returnUrl : null);
            return Ok(new { loginUrl });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating login URL");
            return this.ApiError("server.internal");
        }
    }

    /// <summary>
    /// Handle Discord OAuth callback
    /// </summary>
    /// <param name="code">Authorization code from Discord</param>
    /// <param name="state">State parameter for CSRF protection</param>
    /// <returns>Redirect to the frontend after issuing authentication cookies</returns>
    [HttpGet("callback")]
    [EnableRateLimiting(RateLimitPolicies.OAuthCallback)]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state = null)
    {
        SetOAuthCallbackSecurityHeaders();
        try
        {
            if (string.IsNullOrEmpty(code) || !IsValidOAuthState(state))
            {
                return OAuthFailureRedirect();
            }

            var cachedState = await CacheManager.GetOrSetAsync<string>(
                $"auth_state_{state}",
                static () => Task.FromResult(string.Empty),
                _timeProvider.GetUtcNow().AddMinutes(1)
            );

            if (string.IsNullOrEmpty(cachedState) || !FixedTimeEquals(cachedState, state!))
            {
                return OAuthFailureRedirect();
            }

            RemoveExpiredStateGuards();
            if (!ConsumedOAuthStates.TryAdd(state!, _timeProvider.GetUtcNow().AddMinutes(5)))
            {
                return OAuthFailureRedirect();
            }

            var returnUrl = await CacheManager.GetOrSetAsync<string>(
                $"auth_return_{state}",
                static () => Task.FromResult(string.Empty),
            _timeProvider.GetUtcNow().AddMinutes(1));
            CacheManager.Remove($"auth_state_{state}");
            CacheManager.Remove($"auth_return_{state}");

            var tokenResponse = await _authService.HandleCallbackAsync(code);
            if (tokenResponse == null || _callbackCookieService == null)
            {
                return OAuthFailureRedirect();
            }

            await _callbackCookieService.SetTokensAsync(tokenResponse, Response, HttpContext.RequestAborted);

            var frontendCallbackUrl = $"{_frontendSettings.BaseUrl}{_frontendSettings.CallbackPath}";
            var finalUrl = IsSafeReturnUrl(returnUrl)
                ? $"{frontendCallbackUrl}?return_url={Uri.EscapeDataString(returnUrl)}"
                : frontendCallbackUrl;

            _logger.LogInformation("User {UserId} authenticated successfully", tokenResponse.User.Id);

            return Redirect(finalUrl);
        }
        catch (Exception)
        {
            _logger.LogError("Error handling OAuth callback");
            return OAuthFailureRedirect();
        }
    }

    /// <summary>
    /// Refresh the session using the HttpOnly refresh-token cookie.
    /// </summary>
    /// <returns>The refreshed session without credentials.</returns>
    [HttpPost("refresh")]
    [EnableRateLimiting(RateLimitPolicies.OAuthRefresh)]
    public async Task<IActionResult> RefreshToken()
    {
        try
        {
            var authCookies = GetAuthCookies();
            var refreshToken = authCookies.GetRefreshToken(HttpContext.Request);
            if (string.IsNullOrEmpty(refreshToken))
            {
                authCookies.DeleteAuthCookies(HttpContext.Response);
                return this.ApiError("auth.refreshTokenInvalid");
            }

            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var tokenResponse = await _authService.RefreshTokenAsync(refreshToken, ipAddress);

            if (tokenResponse == null)
            {
                authCookies.DeleteAuthCookies(HttpContext.Response);
                return this.ApiError("auth.refreshTokenInvalid");
            }

            authCookies.SetAuthCookies(HttpContext.Response, tokenResponse);
            return Ok(CreateSessionResponse(tokenResponse.User, tokenResponse.ExpiresAt));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing token");
            _authCookies?.DeleteAuthCookies(HttpContext.Response);
            return this.ApiError("server.internal");
        }
    }

    /// <summary>
    /// Logout user by revoking the refresh-token family represented by the HttpOnly cookie.
    /// </summary>
    /// <returns>No content. Logout is idempotent.</returns>
    [HttpPost("logout")]
    [EnableRateLimiting(RateLimitPolicies.OAuthLogout)]
    public async Task<IActionResult> Logout()
    {
        try
        {
            var authCookies = GetAuthCookies();
            var refreshToken = authCookies.GetRefreshToken(HttpContext.Request);
            if (!string.IsNullOrEmpty(refreshToken))
            {
                await _authService.RevokeTokenAsync(refreshToken);
            }

            authCookies.DeleteAuthCookies(HttpContext.Response);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout");
            _authCookies?.DeleteAuthCookies(HttpContext.Response);
            return this.ApiError("server.internal");
        }
    }

    /// <summary>
    /// Get current user information (requires authentication)
    /// </summary>
    /// <returns>Current user data</returns>
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> GetCurrentUser()
    {
        try
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return this.ApiError("auth.tokenInvalid");
            }

            var user = await _authService.GetUserAsync(userId);
            if (user == null)
            {
                return this.ApiError("user.notFound");
            }

            var userDto = new DiscordUserDto
            {
                Id = user.Id!,
                DiscordId = user.DiscordId,
                Username = user.Username,
                DisplayName = user.DisplayName,
                Email = user.Email,
                Avatar = user.Avatar,
                Verified = user.Verified,
                IsBotOperator = await IsBotOperatorAsync(user.DiscordId, HttpContext.RequestAborted)
            };

            return Ok(userDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current user");
            return this.ApiError("server.internal");
        }
    }

    /// <summary>
    /// Validate the current JWT token and return session state without credentials.
    /// </summary>
    /// <returns>Current session state.</returns>
    [HttpGet("validate")]
    [Authorize]
    public async Task<IActionResult> ValidateToken()
    {
        try
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return this.ApiError("auth.tokenInvalid");
            }

            var user = await _authService.GetUserAsync(userId);
            if (user == null)
            {
                return this.ApiError("auth.tokenInvalid");
            }

            var expClaim = User.FindFirst(JwtRegisteredClaimNames.Exp)?.Value;
            if (!long.TryParse(expClaim, out var exp))
            {
                return this.ApiError("auth.tokenInvalid");
            }

            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp);
            if (expiresAt <= _timeProvider.GetUtcNow()) return this.ApiError("auth.tokenInvalid");

            var userDto = new DiscordUserDto
            {
                Id = user.Id!,
                DiscordId = user.DiscordId,
                Username = user.Username,
                DisplayName = user.DisplayName,
                Email = user.Email,
                Avatar = user.Avatar,
                Verified = user.Verified,
                IsBotOperator = await IsBotOperatorAsync(user.DiscordId, HttpContext.RequestAborted)
            };

            return Ok(CreateSessionResponse(userDto, expiresAt));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying token");
            return this.ApiError("server.internal");
        }
    }

    /// <summary>
    /// Get user's Discord guilds (requires authentication)
    /// </summary>
    /// <returns>List of user's Discord guilds</returns>
    [HttpGet("guilds")]
    [Authorize]
    public async Task<IActionResult> GetUserGuilds([FromQuery] bool refresh = false)
    {
        try
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return this.ApiError("auth.tokenInvalid");
            }

            var guilds = await _authService.GetUserGuildsAsync(userId, refresh);
            if (guilds == null)
            {
                return this.ApiError("auth.guildsUnavailable");
            }

            return Ok(guilds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user guilds");
            return this.ApiError("server.internal");
        }
    }

    [HttpGet("bot-invite")]
    public IActionResult GetBotInviteUrl() => Ok(new { inviteUrl = _authService.GetBotInviteUrl() });

    private IActionResult OAuthFailureRedirect()
    {
        var error = ApiErrorCatalog.Get("auth.oauthFailed");
        var errorUrl = $"{_frontendSettings.BaseUrl}{_frontendSettings.CallbackPath}?errorKey={Uri.EscapeDataString(error.Key)}&message={Uri.EscapeDataString(error.Message)}";
        return Redirect(errorUrl);
    }

    private async Task<bool> IsBotOperatorAsync(string discordId, CancellationToken cancellationToken) =>
        ulong.TryParse(discordId, out var userId) && (await _botOperatorAccess.GetAccessAsync(userId, cancellationToken)).IsAuthorized;

    private static SessionResponse CreateSessionResponse(DiscordUserDto user, DateTime expiresAt) =>
        CreateSessionResponse(user, new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)));

    private static SessionResponse CreateSessionResponse(DiscordUserDto user, DateTimeOffset expiresAt) =>
        new() { User = user, ExpiresAt = expiresAt };

    private IAuthCookieService GetAuthCookies() =>
        _authCookies ?? throw new InvalidOperationException("Authentication cookie service is not configured.");

    private void SetOAuthCallbackSecurityHeaders()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private static bool IsValidOAuthState(string? state) =>
        state?.Length == 36 && Guid.TryParseExact(state, "D", out _);

    private static bool FixedTimeEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    private void RemoveExpiredStateGuards()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var entry in ConsumedOAuthStates)
        {
            if (entry.Value <= now)
            {
                ((ICollection<KeyValuePair<string, DateTimeOffset>>)ConsumedOAuthStates).Remove(entry);
            }
        }
    }

    private static bool IsSafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)
            || returnUrl.Any(char.IsControl)
            || returnUrl.Contains('\\')
            || !Uri.TryCreate(returnUrl, UriKind.Relative, out _))
        {
            return false;
        }

        var decoded = Uri.UnescapeDataString(returnUrl);
        return decoded.StartsWith('/') && !decoded.StartsWith("//") && !decoded.Contains('\\');
    }

}

/// <summary>Issues BFF-owned authentication cookies after a successful OAuth callback.</summary>
public interface IOAuthCallbackCookieService
{
    Task SetTokensAsync(TokenResponse tokens, HttpResponse response, CancellationToken cancellationToken = default);
}

/// <summary>Defines the BFF-owned cookie boundary used by refresh and logout endpoints.</summary>
public interface IAuthCookieService
{
    string? GetRefreshToken(HttpRequest request);
    void SetAuthCookies(HttpResponse response, TokenResponse tokens);
    void DeleteAuthCookies(HttpResponse response);
}
