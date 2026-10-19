using System.Net;
using System.Text.Json;
using Discord;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rankoon.Data.Operations;
using Xunit;

namespace Backend.Tests;

public sealed class ApiExceptionPipelineTests : IClassFixture<RankoonApplicationFactory>
{
    private readonly RankoonApplicationFactory _factory;
    public ApiExceptionPipelineTests(RankoonApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("discord", 422, "discord.missingAccess")]
    [InlineData("timeout", 504, "request.timeout")]
    [InlineData("unknown", 500, "server.internal")]
    public async Task Unhandled_failures_return_safe_canonical_errors_and_record_original_cause(string failure, int status, string key)
    {
        var recorder = new RecordingErrors();
        using var application = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(ApiExceptionProbeController).Assembly);
            services.RemoveAll<IOperationalErrorRecorder>();
            services.AddSingleton<IOperationalErrorRecorder>(recorder);
        }));
        using var client = application.CreateClient();
        using var response = await client.GetAsync("/api/test-exceptions/" + failure);
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(key, json.RootElement.GetProperty("errorKey").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("private", text);
        Assert.NotNull(recorder.Last);
        Assert.Contains("private", recorder.Last!.Exception.Message);
    }

    private sealed class RecordingErrors : IOperationalErrorRecorder
    {
        public long PersistenceFailureCount => 0;
        public OperationalErrorWrite? Last { get; private set; }
        public Task<bool> RecordAsync(OperationalErrorWrite error, CancellationToken cancellationToken = default)
        {
            Last = error;
            return Task.FromResult(true);
        }
    }
}

// Discovered only by the explicit application part in this test host.
[ApiController, AllowAnonymous, Route("api/test-exceptions")]
public sealed class ApiExceptionProbeController : ControllerBase
{
    [HttpGet("{failure}")]
    public IActionResult Fail(string failure) => throw failure switch
    {
        "discord" => new InvalidOperationException("private hub wrapper", new Discord.Net.HttpException(HttpStatusCode.Forbidden, null!, (DiscordErrorCode)50001, "private discord details")),
        "timeout" => new TimeoutException("private timeout details"),
        _ => new Exception("private unknown details")
    };
}
