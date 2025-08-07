namespace Rankoon.Data.Auth;

/// <summary>
/// Response from Discord OAuth token endpoint
/// </summary>
public record DiscordTokenResponse
{
    public string access_token { get; init; } = string.Empty;
    public string token_type { get; init; } = string.Empty;
    public int expires_in { get; init; }
    public string refresh_token { get; init; } = string.Empty;
    public string scope { get; init; } = string.Empty;
}

/// <summary>
/// Discord user information from the API
/// </summary>
public record DiscordUserInfo
{
    public string id { get; init; } = string.Empty;
    public string username { get; init; } = string.Empty;
    public string? discriminator { get; init; }
    public string? global_name { get; init; }
    public string? avatar { get; init; }
    public bool? bot { get; init; }
    public bool? system { get; init; }
    public bool? mfa_enabled { get; init; }
    public string? banner { get; init; }
    public int? accent_color { get; init; }
    public string? locale { get; init; }
    public bool? verified { get; init; }
    public string? email { get; init; }
    public int? flags { get; init; }
    public int? premium_type { get; init; }
    public int? public_flags { get; init; }
}

/// <summary>
/// Our JWT token response
/// </summary>
public record TokenResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
    public DiscordUserDto User { get; init; } = new();
}

/// <summary>
/// Simplified user DTO for responses
/// </summary>
public record DiscordUserDto
{
    public string Id { get; init; } = string.Empty;
    public string DiscordId { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public string? Avatar { get; init; }
    public bool Verified { get; init; }
}

/// <summary>
/// Request for refreshing tokens
/// </summary>
public record RefreshTokenRequest
{
    public string RefreshToken { get; init; } = string.Empty;
}
