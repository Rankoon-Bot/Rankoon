using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Rankoon.Data.Auth;

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
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Handle Discord OAuth callback
    /// </summary>
    /// <param name="code">Authorization code from Discord</param>
    /// <param name="state">State parameter for CSRF protection</param>
    /// <returns>Redirect to frontend with token</returns>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string code, [FromQuery] string? state = null)
    {
        try
        {
            if (string.IsNullOrEmpty(code))
            {
                return BadRequest(new { error = "Authorization code is required" });
            }

            var tokenResponse = await _authService.HandleCallbackAsync(code, state);
            if (tokenResponse == null)
            {
                return BadRequest(new { error = "Authentication failed" });
            }

            // Extract return URL from state if present
            string? returnUrl = null;
            if (!string.IsNullOrEmpty(state) && state.Contains('|'))
            {
                var parts = state.Split('|', 2);
                if (parts.Length == 2)
                {
                    returnUrl = parts[1];
                }
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
            var errorUrl = $"{_frontendSettings.BaseUrl}{_frontendSettings.CallbackPath}?error=authentication_failed";
            return Redirect(errorUrl);
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
                return BadRequest(new { error = "Refresh token is required" });
            }

            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var tokenResponse = await _authService.RefreshTokenAsync(request.RefreshToken, ipAddress);
            
            if (tokenResponse == null)
            {
                return Unauthorized(new { error = "Invalid or expired refresh token" });
            }

            return Ok(tokenResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing token");
            return StatusCode(500, new { error = "Internal server error" });
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
                return BadRequest(new { error = "Refresh token is required" });
            }

            var success = await _authService.RevokeTokenAsync(request.RefreshToken);
            
            if (success)
            {
                return Ok(new { message = "Logged out successfully" });
            }
            else
            {
                return BadRequest(new { error = "Failed to logout" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout");
            return StatusCode(500, new { error = "Internal server error" });
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
                return Unauthorized(new { error = "Invalid token" });
            }

            var user = await _authService.GetUserAsync(userId);
            if (user == null)
            {
                return NotFound(new { error = "User not found" });
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
            return StatusCode(500, new { error = "Internal server error" });
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
                return Unauthorized(new { error = "Invalid token" });
            }

            var user = await _authService.GetUserAsync(userId);
            if (user == null)
            {
                return Unauthorized(new { error = "User not found" });
            }

            // Get the current token from Authorization header
            var authHeader = HttpContext.Request.Headers.Authorization.FirstOrDefault();
            var token = authHeader?.Substring("Bearer ".Length).Trim();
            
            if (string.IsNullOrEmpty(token))
            {
                return Unauthorized(new { error = "No token provided" });
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
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

}
