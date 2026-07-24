using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using Discord;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Rankoon.Data.Auth;
using Xunit;

namespace Backend.Tests;

public sealed class ApiPipelineIntegrationTests : IClassFixture<RankoonApplicationFactory>
{
    private readonly HttpClient _client;

    public ApiPipelineIntegrationTests(RankoonApplicationFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task Unknown_api_route_returns_canonical_json_404_instead_of_spa()
    {
        using var response = await _client.GetAsync("/api/route-that-does-not-exist");

        await AssertCanonicalErrorAsync(response, HttpStatusCode.NotFound, "resource.notFound");
        Assert.DoesNotContain("Rankoon test SPA", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Known_route_with_unsupported_method_returns_canonical_json_405()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        using var response = await _client.SendAsync(request);

        await AssertCanonicalErrorAsync(response, HttpStatusCode.MethodNotAllowed, "request.methodNotAllowed");
        Assert.Contains("GET", response.Content.Headers.Allow);
    }

    [Fact]
    public async Task Malformed_json_returns_canonical_validation_error()
    {
        using var content = new StringContent("{\"refreshToken\":", Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync("/api/auth/refresh", content);

        var error = await AssertCanonicalErrorAsync(response, HttpStatusCode.BadRequest, "request.malformedJson");
        Assert.Equal(JsonValueKind.Object, error.GetProperty("errors").ValueKind);
    }

    [Fact]
    public async Task Missing_request_body_returns_canonical_model_validation_error()
    {
        using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync("/api/auth/refresh", content);

        var error = await AssertCanonicalErrorAsync(response, HttpStatusCode.BadRequest, "request.validationFailed");
        Assert.Equal(JsonValueKind.Object, error.GetProperty("errors").ValueKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not.a.valid.jwt")]
    public async Task Missing_or_invalid_jwt_returns_canonical_challenge(string? accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        if (accessToken != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _client.SendAsync(request);

        await AssertCanonicalErrorAsync(response, HttpStatusCode.Unauthorized, "auth.unauthorized");
    }

    [Fact]
    public async Task Bodyless_controller_not_found_is_transformed_by_result_filter()
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/guilds/1/leaderboard-settings");
        using var response = await _client.SendAsync(request);

        await AssertCanonicalErrorAsync(response, HttpStatusCode.NotFound, "resource.notFound");
    }

    [Fact]
    public async Task Info_route_returns_the_running_build_version()
    {
        using var response = await _client.GetAsync("/api/info");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("buildVersion").GetString()));
    }

    [Fact]
    public async Task Development_mock_endpoints_are_not_available_outside_development()
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/dev/guilds/1/leaderboard-mocks");
        using var response = await _client.SendAsync(request);

        await AssertCanonicalErrorAsync(response, HttpStatusCode.NotFound, "resource.notFound");
    }

    [Fact]
    public async Task Development_mock_endpoints_are_hidden_from_anonymous_clients_outside_development()
    {
        using var response = await _client.GetAsync("/api/dev/guilds/1/leaderboard-mocks");

        await AssertCanonicalErrorAsync(response, HttpStatusCode.NotFound, "resource.notFound");
    }

    [Fact]
    public async Task Unhandled_exception_returns_safe_canonical_error()
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/guilds/1/xp/import/mee6");
        request.Content = new StringContent("{\"guild\":{\"id\":1},\"players\":[]}", Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request);

        var error = await AssertCanonicalErrorAsync(response, HttpStatusCode.InternalServerError, "server.internal");
        Assert.Equal("An unexpected server error occurred.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Rate_limit_rejection_returns_retry_after_and_canonical_429()
    {
        const string path = "/api/guilds/1/reports/activity";
        using var firstResponse = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, firstResponse.StatusCode);

        using var response = await _client.GetAsync(path);

        var error = await AssertCanonicalErrorAsync(response, HttpStatusCode.TooManyRequests, "rateLimit.exceeded");
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.True(response.Headers.RetryAfter.Delta > TimeSpan.Zero);
        Assert.True(error.GetProperty("parameters").GetProperty("retryAfterSeconds").GetInt32() > 0);
    }

    [Fact]
    public async Task Non_api_client_route_still_uses_spa_fallback()
    {
        using var response = await _client.GetAsync("/client-side-route");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Rankoon test SPA", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_static_asset_is_served_instead_of_spa_fallback()
    {
        using var response = await _client.GetAsync("/test.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("console.log('static asset');", (await response.Content.ReadAsStringAsync()).Trim());
    }

    [Fact]
    public async Task Anonymous_leaderboard_hub_can_negotiate()
    {
        using var response = await _client.PostAsync("/hubs/leaderboard/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("connectionToken", out _));
    }

    [Fact]
    public async Task Missing_static_asset_returns_404_instead_of_spa_fallback()
    {
        using var response = await _client.GetAsync("/missing.js");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Rankoon test SPA", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string path)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("integration-test-secret-key-that-is-long-enough"));
        var token = new JwtSecurityToken(
            issuer: "Rankoon.Tests",
            audience: "Rankoon.Tests",
            claims: [new Claim("discord_id", "123")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return request;
    }

    private static async Task<JsonElement> AssertCanonicalErrorAsync(HttpResponseMessage response, HttpStatusCode statusCode, string errorKey)
    {
        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(errorKey, root.GetProperty("errorKey").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        Assert.False(root.TryGetProperty("error", out _));
        return root.Clone();
    }
}

public sealed class RankoonApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseWebRoot(Path.Combine(AppContext.BaseDirectory, "TestWebRoot"));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGuildAuthorizationService>();
            services.AddSingleton<IGuildAuthorizationService, AllowGuildAuthorizationService>();
        });
    }
}

internal sealed class AllowGuildAuthorizationService : IGuildAuthorizationService
{
    public Task<IGuildUser?> ResolveMemberAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default) => Task.FromResult<IGuildUser?>(null);
    public Task<bool> IsOwnerAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> IsMemberAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> CanAccessAnyModuleAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> CanAccessModuleAsync(ClaimsPrincipal user, ulong guildId, string moduleId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<IReadOnlyList<string>> GetAccessibleModuleIdsAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([GuildModuleIds.Xp, GuildModuleIds.Leaderboard, GuildModuleIds.VoiceHubs, GuildModuleIds.Reporting]);
    public ulong? GetDiscordUserId(ClaimsPrincipal user) => 123;
}
