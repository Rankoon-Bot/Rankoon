using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rankoon.Controllers;
using Rankoon.Data.Auth;
using Rankoon.Data.Model;
using Rankoon.Data.Utils;
using Xunit;

namespace Backend.Tests;

public sealed class AuthSessionControllerTests
{
    [Fact]
    public async Task Refresh_uses_cookie_and_returns_session_without_credentials()
    {
        var auth = new StubAuthService { RefreshResponse = TokenResponse() };
        var cookies = new RecordingAuthCookies { RefreshToken = "refresh-cookie" };
        var result = await CreateController(auth, cookies).RefreshToken();

        var response = Assert.IsType<OkObjectResult>(result);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Value)).RootElement;
        Assert.Equal("refresh-cookie", auth.RefreshedToken);
        Assert.Equal(1, cookies.SetCount);
        Assert.False(json.TryGetProperty("accessToken", out _));
        Assert.False(json.TryGetProperty("refreshToken", out _));
        Assert.False(json.TryGetProperty("token", out _));
        Assert.True(json.TryGetProperty("user", out _));
        Assert.True(json.TryGetProperty("expiresAt", out _));
    }

    [Fact]
    public async Task Refresh_replay_failure_clears_cookies_and_has_stable_error()
    {
        var auth = new StubAuthService();
        var cookies = new RecordingAuthCookies { RefreshToken = "replayed-cookie" };
        var result = await CreateController(auth, cookies).RefreshToken();

        var error = Assert.IsType<ObjectResult>(result);
        var value = Assert.IsType<Rankoon.Api.ApiErrorResponse>(error.Value);
        Assert.Equal(StatusCodes.Status401Unauthorized, error.StatusCode);
        Assert.Equal("auth.refreshTokenInvalid", value.ErrorKey);
        Assert.Equal("replayed-cookie", auth.RefreshedToken);
        Assert.Equal(1, cookies.DeleteCount);
    }

    [Fact]
    public async Task Logout_without_cookie_is_idempotent_and_clears_cookies()
    {
        var auth = new StubAuthService();
        var cookies = new RecordingAuthCookies();
        var result = await CreateController(auth, cookies).Logout();

        Assert.IsType<NoContentResult>(result);
        Assert.Null(auth.RevokedToken);
        Assert.Equal(1, cookies.DeleteCount);
    }

    [Fact]
    public async Task Validate_requires_a_valid_expiration_claim_without_fallback()
    {
        var controller = CreateController(new StubAuthService { User = User() }, new RecordingAuthCookies());
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-id")], "test"));

        var result = await controller.ValidateToken();

        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal("auth.tokenInvalid", Assert.IsType<Rankoon.Api.ApiErrorResponse>(error.Value).ErrorKey);
    }

    [Fact]
    public async Task Validate_returns_the_validated_claim_expiry_without_a_token()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var controller = CreateController(new StubAuthService { User = User() }, new RecordingAuthCookies());
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-id"), new Claim("exp", expiresAt.ToString())], "test"));

        var result = await controller.ValidateToken();

        var response = Assert.IsType<OkObjectResult>(result);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Value)).RootElement;
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expiresAt), json.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.False(json.TryGetProperty("token", out _));
        Assert.False(json.TryGetProperty("accessToken", out _));
        Assert.False(json.TryGetProperty("refreshToken", out _));
    }

    [Fact]
    public void Refresh_and_logout_do_not_accept_a_body_token_contract()
    {
        var refresh = typeof(AuthController).GetMethod(nameof(AuthController.RefreshToken))!;
        var logout = typeof(AuthController).GetMethod(nameof(AuthController.Logout))!;

        Assert.Empty(refresh.GetParameters());
        Assert.Empty(logout.GetParameters());
        Assert.Null(typeof(AuthController).Assembly.GetType("Rankoon.Data.Auth.RefreshTokenRequest"));
    }

    private static AuthController CreateController(StubAuthService auth, RecordingAuthCookies cookies) => new(
        auth,
        Options.Create(new FrontendSettings()),
        TimeProvider.System,
        NullLogger<AuthController>.Instance,
        new StubBotOperatorAccessService(),
        authCookies: cookies)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    private static TokenResponse TokenResponse() => new()
    {
        AccessToken = "access-secret",
        RefreshToken = "refresh-secret",
        ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        User = new DiscordUserDto { Id = "user-id", DiscordId = "1", Username = "user" }
    };

    private static DiscordUser User() => new() { Id = "user-id", DiscordId = "1", Username = "user" };

    private sealed class RecordingAuthCookies : IAuthCookieService
    {
        public string? RefreshToken { get; init; }
        public int SetCount { get; private set; }
        public int DeleteCount { get; private set; }
        public string? GetRefreshToken(HttpRequest request) => RefreshToken;
        public void SetAuthCookies(HttpResponse response, TokenResponse tokens) => SetCount++;
        public void DeleteAuthCookies(HttpResponse response) => DeleteCount++;
    }

    private sealed class StubAuthService : IAuthService
    {
        public TokenResponse? RefreshResponse { get; init; }
        public DiscordUser? User { get; init; }
        public string? RefreshedToken { get; private set; }
        public string? RevokedToken { get; private set; }
        public string GetLoginUrl(string? returnUrl = null) => string.Empty;
        public Task<TokenResponse?> HandleCallbackAsync(string code) => Task.FromResult<TokenResponse?>(null);
        public Task<TokenResponse?> RefreshTokenAsync(string refreshToken, string? ipAddress = null)
        {
            RefreshedToken = refreshToken;
            return Task.FromResult(RefreshResponse);
        }
        public Task<bool> RevokeTokenAsync(string refreshToken)
        {
            RevokedToken = refreshToken;
            return Task.FromResult(true);
        }
        public Task<DiscordUser?> GetUserAsync(string userId) => Task.FromResult(User);
        public Task<GuildDto[]?> GetUserGuildsAsync(string userId, bool refresh = false) => Task.FromResult<GuildDto[]?>(null);
        public string GetBotInviteUrl() => string.Empty;
    }

    private sealed class StubBotOperatorAccessService : IBotOperatorAccessService
    {
        public Task<BotOperatorAccessResult> GetAccessAsync(ulong discordUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BotOperatorAccessResult(false, null));
        public Task WarmAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
