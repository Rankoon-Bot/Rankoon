using Microsoft.AspNetCore.DataProtection;
using Rankoon.Data.Model;
using System.Security.Cryptography;

namespace Rankoon.Data.Discord;

public interface IDiscordOAuthTokenProtector
{
    string ProtectAccessToken(string token);
    string ProtectRefreshToken(string token);
    string? ReadAccessToken(DiscordUser user);
    string? ReadRefreshToken(DiscordUser user);
}

/// <summary>Protects Discord OAuth credentials with independent, versioned purposes.</summary>
public sealed class DiscordOAuthTokenProtector : IDiscordOAuthTokenProtector
{
    public const int CurrentVersion = 1;
    private readonly IDataProtector accessTokens;
    private readonly IDataProtector refreshTokens;

    public DiscordOAuthTokenProtector(IDataProtectionProvider dataProtection)
    {
        accessTokens = dataProtection.CreateProtector("Rankoon", "Discord", "OAuth", "AccessToken", "v1");
        refreshTokens = dataProtection.CreateProtector("Rankoon", "Discord", "OAuth", "RefreshToken", "v1");
    }

    public string ProtectAccessToken(string token) => Protect(accessTokens, token);
    public string ProtectRefreshToken(string token) => Protect(refreshTokens, token);

    public string? ReadAccessToken(DiscordUser user) => Read(user.ProtectedAccessToken, user.OAuthTokenProtectionVersion, accessTokens);
    public string? ReadRefreshToken(DiscordUser user) => Read(user.ProtectedRefreshToken, user.OAuthTokenProtectionVersion, refreshTokens);

    private static string Protect(IDataProtector protector, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        try { return protector.Protect(token); }
        catch (CryptographicException exception) { throw new DiscordOAuthTokenProtectionException("Discord OAuth token protection failed.", exception); }
    }

    private static string? Read(string? value, int? version, IDataProtector protector)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (version is null) return value; // Legacy plaintext; the migration replaces it atomically.
        if (version != CurrentVersion) throw new DiscordOAuthTokenProtectionException($"Unsupported Discord OAuth token protection version {version}.");
        try { return protector.Unprotect(value); }
        catch (CryptographicException exception) { throw new DiscordOAuthTokenProtectionException("Discord OAuth token decryption failed. Verify the data-protection key ring.", exception); }
    }
}

public sealed class DiscordOAuthTokenProtectionException(string message, Exception? innerException = null) : Exception(message, innerException);
