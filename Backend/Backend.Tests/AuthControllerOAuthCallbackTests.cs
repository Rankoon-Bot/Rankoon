using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rankoon.Controllers;
using Rankoon.Data.Auth;
using Rankoon.Data.Model;
using Xunit;

namespace Backend.Tests;

public sealed class AuthControllerOAuthCallbackTests
{
    [Fact]
    public async Task Callback_issues_cookies_and_redirects_without_tokens()
    {
        var state = Guid.NewGuid().ToString();
        StoreState(state, "/dashboard");
        var cookies = new RecordingCookieService();
        var controller = CreateController(cookies);

        var result = await controller.Callback("discord-authorization-code", state);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("https://rankoon.example/auth/callback?return_url=%2Fdashboard", redirect.Url);
        Assert.DoesNotContain("rankoon-access-token", redirect.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("rankoon-refresh-token", redirect.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("discord-authorization-code", redirect.Url, StringComparison.Ordinal);
        Assert.NotNull(cookies.Tokens);
        Assert.Equal("no-store, no-cache, max-age=0", controller.Response.Headers.CacheControl);
        Assert.Equal("no-cache", controller.Response.Headers.Pragma);
        Assert.Equal("no-referrer", controller.Response.Headers["Referrer-Policy"]);
    }

    [Fact]
    public async Task Callback_rejects_a_state_after_its_first_use()
    {
        var state = Guid.NewGuid().ToString();
        StoreState(state, "/dashboard");
        var cookies = new RecordingCookieService();
        var controller = CreateController(cookies);

        var first = await controller.Callback("discord-authorization-code", state);
        var second = await CreateController(cookies).Callback("discord-authorization-code", state);

        Assert.IsType<RedirectResult>(first);
        var replay = Assert.IsType<RedirectResult>(second);
        Assert.Equal("https://rankoon.example/auth/callback?errorKey=auth.oauthFailed&message=Authentication%20could%20not%20be%20completed.%20Please%20try%20again.", replay.Url);
        Assert.Equal(1, cookies.CallCount);
    }

    [Theory]
    [InlineData("//attacker.example")]
    [InlineData("/%2f%2fattacker.example")]
    [InlineData("/\\attacker.example")]
    public void Login_does_not_bind_an_unsafe_return_route(string returnUrl)
    {
        var auth = new StubAuthService();
        var controller = CreateController(new RecordingCookieService(), auth);

        var result = controller.GetLoginUrl(returnUrl);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(auth.LoginReturnUrl);
    }

    private static void StoreState(string state, string returnUrl)
    {
        states.Store(state, returnUrl, DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static readonly IOAuthStateStore states = new OAuthStateStore(TimeProvider.System);

    private static AuthController CreateController(RecordingCookieService cookies, StubAuthService? auth = null)
    {
        var controller = new AuthController(
            auth ?? new StubAuthService(),
            Options.Create(new FrontendSettings { BaseUrl = "https://rankoon.example", CallbackPath = "/auth/callback" }),
            TimeProvider.System,
            NullLogger<AuthController>.Instance,
            new StubBotOperatorAccessService(),
            states,
            cookies)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return controller;
    }

    private sealed class RecordingCookieService : IOAuthCallbackCookieService
    {
        public TokenResponse? Tokens { get; private set; }
        public int CallCount { get; private set; }

        public Task SetTokensAsync(TokenResponse tokens, HttpResponse response, CancellationToken cancellationToken = default)
        {
            Tokens = tokens;
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class StubAuthService : IAuthService
    {
        public string? LoginReturnUrl { get; private set; }

        public string GetLoginUrl(string? returnUrl = null)
        {
            LoginReturnUrl = returnUrl;
            return "https://discord.example/oauth";
        }

        public Task<TokenResponse?> HandleCallbackAsync(string code) => Task.FromResult<TokenResponse?>(new TokenResponse
        {
            AccessToken = "rankoon-access-token",
            RefreshToken = "rankoon-refresh-token",
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });

        public Task<TokenResponse?> RefreshTokenAsync(string refreshToken, string? ipAddress = null) => throw new NotImplementedException();
        public Task<bool> RevokeTokenAsync(string refreshToken) => throw new NotImplementedException();
        public Task<DiscordUser?> GetUserAsync(string userId) => throw new NotImplementedException();
        public Task<GuildDto[]?> GetUserGuildsAsync(string userId, bool refresh = false) => throw new NotImplementedException();
        public string GetBotInviteUrl() => throw new NotImplementedException();
    }

    private sealed class StubBotOperatorAccessService : IBotOperatorAccessService
    {
        public Task<BotOperatorAccessResult> GetAccessAsync(ulong discordUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BotOperatorAccessResult(false, null));

        public Task WarmAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
