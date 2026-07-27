using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Xunit;

namespace Backend.Tests;

public sealed class DiscordOAuthTokenProtectionTests
{
    private readonly IDiscordOAuthTokenProtector tokens = new DiscordOAuthTokenProtector(DataProtectionProvider.Create("discord-oauth-token-protection-tests"));

    [Fact]
    public void Protected_user_BSON_never_contains_plaintext_OAuth_tokens()
    {
        const string accessToken = "discord-access-token-secret";
        const string refreshToken = "discord-refresh-token-secret";
        var user = new DiscordUser
        {
            ProtectedAccessToken = tokens.ProtectAccessToken(accessToken),
            ProtectedRefreshToken = tokens.ProtectRefreshToken(refreshToken),
            OAuthTokenProtectionVersion = DiscordOAuthTokenProtector.CurrentVersion
        };

        var document = user.ToBsonDocument();
        var bson = document.ToJson();

        Assert.DoesNotContain(accessToken, bson);
        Assert.DoesNotContain(refreshToken, bson);
        Assert.Equal(DiscordOAuthTokenProtector.CurrentVersion, document["oauth_token_protection_version"].ToInt32());
        Assert.Equal(accessToken, tokens.ReadAccessToken(user));
        Assert.Equal(refreshToken, tokens.ReadRefreshToken(user));
    }

    [Fact]
    public void Access_and_refresh_tokens_cannot_be_unprotected_with_each_others_purpose()
    {
        var user = new DiscordUser
        {
            ProtectedAccessToken = tokens.ProtectAccessToken("access"),
            ProtectedRefreshToken = tokens.ProtectRefreshToken("refresh"),
            OAuthTokenProtectionVersion = DiscordOAuthTokenProtector.CurrentVersion
        };

        Assert.Throws<DiscordOAuthTokenProtectionException>(() => tokens.ReadRefreshToken(new DiscordUser
        {
            ProtectedRefreshToken = user.ProtectedAccessToken,
            OAuthTokenProtectionVersion = user.OAuthTokenProtectionVersion
        }));
        Assert.Throws<DiscordOAuthTokenProtectionException>(() => tokens.ReadAccessToken(new DiscordUser
        {
            ProtectedAccessToken = user.ProtectedRefreshToken,
            OAuthTokenProtectionVersion = user.OAuthTokenProtectionVersion
        }));
    }

    [Fact]
    public void Legacy_token_migration_is_idempotent_and_preserves_refresh_token()
    {
        var user = new DiscordUser { ProtectedAccessToken = "legacy-access", ProtectedRefreshToken = "legacy-refresh" };

        Assert.True(DiscordOAuthTokenMigrationService.ProtectLegacyUserTokens(user, tokens));
        Assert.False(DiscordOAuthTokenMigrationService.ProtectLegacyUserTokens(user, tokens));
        Assert.Equal(DiscordOAuthTokenProtector.CurrentVersion, user.OAuthTokenProtectionVersion);
        Assert.Equal("legacy-access", tokens.ReadAccessToken(user));
        Assert.Equal("legacy-refresh", tokens.ReadRefreshToken(user));
    }

    [Fact]
    public void Migration_keeps_an_absent_refresh_token_absent()
    {
        var user = new DiscordUser { ProtectedAccessToken = "legacy-access" };

        Assert.True(DiscordOAuthTokenMigrationService.ProtectLegacyUserTokens(user, tokens));

        Assert.Null(user.ProtectedRefreshToken);
        Assert.Null(tokens.ReadRefreshToken(user));
    }

    [Fact]
    public void An_unavailable_key_ring_fails_with_a_controlled_exception()
    {
        var user = new DiscordUser
        {
            ProtectedAccessToken = tokens.ProtectAccessToken("access"),
            OAuthTokenProtectionVersion = DiscordOAuthTokenProtector.CurrentVersion
        };
        var wrongKeyRing = new DiscordOAuthTokenProtector(DataProtectionProvider.Create("discord-oauth-token-protection-wrong-key-ring"));

        Assert.Throws<DiscordOAuthTokenProtectionException>(() => wrongKeyRing.ReadAccessToken(user));
    }
}
