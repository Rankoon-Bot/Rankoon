using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Rankoon.Data.Auth;
using Rankoon.Data.Utils;
using Rankoon.Api;

namespace Rankoon.Controllers;

/// <summary>
/// Controller for handling authentication operations
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly FrontendSettings _frontendSettings;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IAuthService authService,
        IOptions<FrontendSettings> frontendSettings,
        ILogger<AuthController> logger)
    {
        _authService = authService;
        _frontendSettings = frontendSettings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Get Discord OAuth login URL
    /// </summary>
    /// <param name="returnUrl">Optional return URL after successful authentication</param>
    /// <returns>Login URL for Discord OAuth</returns>
    [HttpGet("login")]
    public IActionResult GetLoginUrl([FromQuery] string? returnUrl = null)
    {
        try
        {
            var loginUrl = _authService.GetLoginUrl(returnUrl);
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
    /// <returns>Redirect to frontend with token</returns>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state = null)
    {
        try
        {
            if (string.IsNullOrEmpty(code))
            {
                return OAuthFailureRedirect();
            }

            string? cached_state = null;
            string? returnUrl = null;
            if (state?.Contains('|') ?? false)
            {
                var parts = state?.Split('|');
                state = parts?[0]; // The actual state
                returnUrl = parts?.Length > 1 ? parts[1] : null;
            }

            cached_state = await CacheManager.GetOrSetAsync<string>(
                $"auth_state_{state}",
                static () => Task.FromResult(string.Empty),
                DateTimeOffset.UtcNow.AddMinutes(1)
            );

            if (string.IsNullOrEmpty(cached_state))
            {
                _logger.LogWarning("Invalid or expired state parameter for Discord OAuth callback");
                throw new InvalidOperationException("Invalid or expired state parameter");
            }

            CacheManager.Remove($"auth_state_{state}");


            if (cached_state != state)
            {
                _logger.LogWarning("State parameter mismatch: expected {ExpectedState}, got {ActualState}", cached_state, state);
                throw new InvalidOperationException("State parameter mismatch");
            }


            var tokenResponse = await _authService.HandleCallbackAsync(code);
            if (tokenResponse == null)
            {
                throw new InvalidOperationException("Failed to handle OAuth callback");
            }


            // Build frontend callback URL with our token
            var frontendCallbackUrl = $"{_frontendSettings.BaseUrl}{_frontendSettings.CallbackPath}";
            var parameters = new List<string>
            {
                $"token={Uri.EscapeDataString(tokenResponse.AccessToken)}",
                $"refresh_token={Uri.EscapeDataString(tokenResponse.RefreshToken)}",
                $"expires_at={Uri.EscapeDataString(tokenResponse.ExpiresAt.ToString("O"))}"
            };

            if (!string.IsNullOrEmpty(returnUrl))
            {
                parameters.Add($"return_url={Uri.EscapeDataString(returnUrl)}");
            }

            var finalUrl = $"{frontendCallbackUrl}?{string.Join("&", parameters)}";

            _logger.LogInformation("User {UserId} authenticated successfully", tokenResponse.User.Id);

            return Redirect(finalUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling OAuth callback");

            // Redirect to frontend with error
            return OAuthFailureRedirect();
        }
    }

    /// <summary>
    /// Refresh JWT tokens using refresh token
    /// </summary>
    /// <param name="request">Refresh token request</param>
    /// <returns>New tokens</returns>
    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.RefreshToken))
            {
                return this.ApiError("auth.refreshTokenRequired");
            }

            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var tokenResponse = await _authService.RefreshTokenAsync(request.RefreshToken, ipAddress);

            if (tokenResponse == null)
            {
                return this.ApiError("auth.refreshTokenInvalid");
            }

            return Ok(tokenResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing token");
            return this.ApiError("server.internal");
        }
    }

    /// <summary>
    /// Logout user by revoking refresh token
    /// </summary>
    /// <param name="request">Refresh token to revoke</param>
    /// <returns>Success status</returns>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshTokenRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.RefreshToken))
            {
                return this.ApiError("auth.refreshTokenRequired");
            }

            var success = await _authService.RevokeTokenAsync(request.RefreshToken);

            if (success)
            {
                return Ok(new { messageKey = "auth.logoutSucceeded", message = "Logged out successfully." });
            }
            else
            {
                return this.ApiError("auth.logoutFailed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout");
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
                Verified = user.Verified
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
    /// Validate if the current JWT token is valid and return token info
    /// </summary>
    /// <returns>Token verification response</returns>
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

            // Get the current token from Authorization header
            var authHeader = HttpContext.Request.Headers.Authorization.FirstOrDefault();
            var token = authHeader?.Substring("Bearer ".Length).Trim();

            if (string.IsNullOrEmpty(token))
            {
                return this.ApiError("auth.tokenMissing");
            }

            // Get token expiration from claims
            var expClaim = User.FindFirst("exp")?.Value;
            DateTime expiresAt = DateTime.UtcNow.AddHours(1); // Default fallback

            if (!string.IsNullOrEmpty(expClaim) && long.TryParse(expClaim, out var exp))
            {
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp).DateTime;
            }

            var userDto = new DiscordUserDto
            {
                Id = user.Id!,
                DiscordId = user.DiscordId,
                Username = user.Username,
                DisplayName = user.DisplayName,
                Email = user.Email,
                Avatar = user.Avatar,
                Verified = user.Verified
            };

            var response = new
            {
                token = token,
                user = userDto,
                expiresAt = expiresAt.ToString("O") // ISO 8601 format
            };

            return Ok(response);
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
    public async Task<IActionResult> GetUserGuilds()
    {
        try
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return this.ApiError("auth.tokenInvalid");
            }

            var guilds = await _authService.GetUserGuildsAsync(userId);
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

    private IActionResult OAuthFailureRedirect()
    {
        var error = ApiErrorCatalog.Get("auth.oauthFailed");
        var errorUrl = $"{_frontendSettings.BaseUrl}{_frontendSettings.CallbackPath}?errorKey={Uri.EscapeDataString(error.Key)}&message={Uri.EscapeDataString(error.Message)}";
        return Redirect(errorUrl);
    }

}
