using System.Net;
using System.Text.Json;
using Discord;
using Microsoft.AspNetCore.Http;
using Rankoon.Api;
using Xunit;

namespace Backend.Tests;

public sealed class ApiExceptionMapperTests
{
    [Theory]
    [InlineData(50001, "discord.missingAccess", 422)]
    [InlineData(50013, "discord.missingPermissions", 422)]
    [InlineData(10003, "discord.resourceUnavailable", 409)]
    [InlineData(30013, "discord.limitReached", 422)]
    [InlineData(50035, "discord.invalidRequest", 422)]
    [InlineData(50014, "discord.authenticationFailed", 503)]
    [InlineData(20028, "discord.rateLimited", 503)]
    public void Discord_cause_survives_service_and_aggregate_wrappers(int code, string key, int status)
    {
        var discord = new Discord.Net.HttpException(HttpStatusCode.Forbidden, null!, (DiscordErrorCode)code, "private discord details");
        var exception = new AggregateException(new Exception("other failure"), new InvalidOperationException("wrapper", discord));
        var result = ApiExceptionMapper.Map(exception);
        Assert.Equal(key, result.Key);
        Assert.Equal(status, result.StatusCode);
    }

    [Theory]
    [InlineData(403, "discord.missingPermissions")]
    [InlineData(404, "discord.resourceUnavailable")]
    [InlineData(429, "discord.rateLimited")]
    [InlineData(503, "discord.unavailable")]
    [InlineData(504, "request.timeout")]
    public void Unknown_Discord_codes_use_HTTP_status(int status, string key)
    {
        var exception = new Discord.Net.HttpException((HttpStatusCode)status, null!, (DiscordErrorCode)999999, "private");
        Assert.Equal(key, ApiExceptionMapper.Map(exception).Key);
    }

    [Fact]
    public void Infrastructure_failures_have_safe_fallbacks()
    {
        Assert.Equal("request.timeout", ApiExceptionMapper.Map(new InvalidOperationException("wrapper", new TimeoutException())).Key);
        Assert.Equal("request.timeout", ApiExceptionMapper.Map(new TaskCanceledException()).Key);
        Assert.Equal("service.unavailable", ApiExceptionMapper.Map(new HttpRequestException("private host")).Key);
        Assert.Equal("server.internal", ApiExceptionMapper.Map(new Exception("Missing Access in user text is not a Discord error")).Key);
        Assert.Equal("server.internal", ApiExceptionMapper.Map(null).Key);
    }

    [Fact]
    public void Only_cancelled_requests_with_cancellation_cause_are_client_cancellations()
    {
        var cancelled = new CancellationToken(true);
        Assert.True(ApiExceptionMapper.IsClientCancellation(new Exception("wrapper", new TaskCanceledException()), cancelled));
        Assert.False(ApiExceptionMapper.IsClientCancellation(new TaskCanceledException(), CancellationToken.None));
        Assert.False(ApiExceptionMapper.IsClientCancellation(new Exception(), cancelled));
    }

    [Fact]
    public async Task Writer_retains_trace_but_never_exposes_exception_messages()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "delete-hub-trace" };
        context.Response.Body = new MemoryStream();
        var discord = new Discord.Net.HttpException(HttpStatusCode.Forbidden, null!, (DiscordErrorCode)50001, "private-token-and-path");
        await ApiExceptionMapper.WriteAsync(context, new InvalidOperationException("private-wrapper", discord));
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(422, context.Response.StatusCode);
        Assert.Equal("discord.missingAccess", json.RootElement.GetProperty("errorKey").GetString());
        Assert.Equal("delete-hub-trace", json.RootElement.GetProperty("traceId").GetString());
        Assert.DoesNotContain("private", json.RootElement.ToString());
    }
}
