using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Rankoon.Api;
using Xunit;

namespace Backend.Tests;

public sealed class RateLimitingPolicyTests
{
    [Fact]
    public void Partition_key_composes_normalized_ip_user_guild_and_hashed_refresh_cookie()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("2001:db8::7");
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("discord_id", "42")], "test"));
        context.Request.RouteValues["guildId"] = "99";
        context.Request.Headers.Cookie = "refresh_token=not-a-limiter-secret";

        var key = RateLimitPolicies.PartitionKey(context, RateLimitPolicies.OAuthRefresh);

        Assert.Contains("user:42", key);
        Assert.Contains("guild:99", key);
        Assert.Contains("ip:2001:db8::7", key);
        Assert.Contains("refresh:", key);
        Assert.DoesNotContain("not-a-limiter-secret", key);
    }

    [Fact]
    public void Invalid_proxy_or_limit_configuration_is_rejected()
    {
        Assert.False(RateLimitPolicies.IsValid(new RateLimitingOptions { TrustedProxyIps = ["not-an-ip"] }));
        Assert.False(RateLimitPolicies.IsValid(new RateLimitingOptions { XpImport = new RateLimitPolicyOptions { PermitLimit = 0 } }));
    }
}
