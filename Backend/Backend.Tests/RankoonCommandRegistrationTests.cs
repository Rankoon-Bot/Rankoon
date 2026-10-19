using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rankoon.Data.Auth;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Xunit;

namespace Backend.Tests;

public sealed class RankoonCommandRegistrationTests
{
    private static readonly BotRuntimeContext Runtime = new("platform", BotIdentityMode.Rankoon, null!, null!);

    [Fact]
    public async Task Failed_registration_is_retryable_and_success_is_cached()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(
            ++calls == 1 ? HttpStatusCode.BadRequest : HttpStatusCode.OK))));
        var registrar = Registrar(client);
        Assert.False(await registrar.RegisterAsync(Runtime));
        Assert.True(await registrar.RegisterAsync(Runtime));
        Assert.True(await registrar.RegisterAsync(Runtime));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Concurrent_registration_waits_for_the_actual_result_and_sends_once()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            started.SetResult();
            return response.Task;
        }));
        var registrar = Registrar(client);
        var first = registrar.RegisterAsync(Runtime);
        await started.Task;
        var second = registrar.RegisterAsync(Runtime);
        Assert.False(second.IsCompleted);
        response.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
        Assert.All(await Task.WhenAll(first, second), Assert.True);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Interrupted_registration_releases_the_gate_and_allows_retry()
    {
        var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            if (++calls == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var registrar = Registrar(client);
        using var cancellation = new CancellationTokenSource();
        var first = registrar.RegisterAsync(Runtime, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.True(await registrar.RegisterAsync(Runtime));
        Assert.Equal(2, calls);
    }

    private static ApplicationCommandRegistrar Registrar(HttpClient client) => new(new RankoonCommandSchemaProvider(),
        Options.Create(new DiscordSettings { ClientId = "123", BotToken = "test-token" }), new Factory(client),
        null!, null!, TimeProvider.System, NullLogger<ApplicationCommandRegistrar>.Instance);

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
