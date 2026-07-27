using Microsoft.Extensions.Caching.Memory;
using Rankoon.Data.Auth;
using Rankoon.Data.Utils;
using Xunit;

namespace Backend.Tests;

public sealed class CacheArchitectureTests
{
    [Fact]
    public async Task GetOrCreateAsync_runs_one_factory_for_concurrent_same_key_requests()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new ApplicationCache(memory);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task<string> GetValue() => cache.GetOrCreateAsync("same-key", async _ =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return "value";
        }, DateTimeOffset.UtcNow.AddMinutes(1));

        var first = GetValue();
        await started.Task;
        var second = GetValue();
        release.SetResult();

        Assert.Equal(["value", "value"], await Task.WhenAll(first, second));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrCreateAsync_does_not_block_different_keys_behind_one_factory()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new ApplicationCache(memory);
        var started = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string> GetValue(string key) => cache.GetOrCreateAsync(key, async _ =>
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
            await release.Task;
            return key;
        }, DateTimeOffset.UtcNow.AddMinutes(1));

        var first = GetValue("first");
        var second = GetValue("second");
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        release.SetResult();

        Assert.Equal(["first", "second"], await Task.WhenAll(first, second));
    }

    [Fact]
    public async Task GetOrCreateAsync_does_not_cache_null_values()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new ApplicationCache(memory);
        var calls = 0;

        Task<string?> GetValue() => cache.GetOrCreateAsync<string?>("null", _ => Task.FromResult<string?>(++calls == 1 ? null : "value"), DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Null(await GetValue());
        Assert.Equal("value", await GetValue());
        Assert.Equal("value", await GetValue());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OAuth_state_can_be_consumed_once_across_concurrent_callbacks()
    {
        var store = new OAuthStateStore(TimeProvider.System);
        var state = Guid.NewGuid().ToString("D");
        store.Store(state, "/dashboard", DateTimeOffset.UtcNow.AddMinutes(5));

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => store.TryConsume(state, out OAuthState? _))));

        Assert.Equal(1, results.Count(result => result));
    }

    [Fact]
    public void OAuth_state_expired_before_callback_is_rejected()
    {
        var store = new OAuthStateStore(TimeProvider.System);
        var state = Guid.NewGuid().ToString("D");
        store.Store(state, "/dashboard", DateTimeOffset.UtcNow.AddTicks(-1));

        Assert.False(store.TryConsume(state, out var consumed));
        Assert.Null(consumed);
    }
}
