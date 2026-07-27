namespace Rankoon.Data.Auth;

/// <summary>
/// Discord OAuth configuration settings
/// </summary>
public class DiscordSettings
{
    public const string SectionName = "Discord";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string BotToken { get; set; } = string.Empty;
    public string BotInvitePermissions { get; set; } = "285215760";
    public string RedirectUri { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = Array.Empty<string>();
}

/// <summary>
/// JWT configuration settings
/// </summary>
public class JwtSettings
{
    public const string SectionName = "Jwt";

    public string SecretKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int AccessTokenExpirationMinutes { get; set; } = 15;
    public int RefreshTokenExpirationDays { get; set; } = 30; // 30 days
}

public sealed class AuthCookieOptions
{
    public const string SectionName = "AuthCookies";
    public string AccessCookieName { get; set; } = "__Host-rankoon-access";
    public string RefreshCookieName { get; set; } = "__Host-rankoon-refresh";
    public string CsrfHeaderName { get; set; } = "X-CSRF-TOKEN";
    public bool Secure { get; set; } = true;
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Strict;
}

/// <summary>
/// Frontend configuration settings
/// </summary>
public class FrontendSettings
{
    public const string SectionName = "Frontend";

    public string BaseUrl { get; set; } = string.Empty;
    public string CallbackPath { get; set; } = "/auth/callback";
}
