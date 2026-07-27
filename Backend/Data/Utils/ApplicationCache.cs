using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace Rankoon.Data.Utils;

public interface IApplicationCache
{
    Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, DateTimeOffset absoluteExpiration, CancellationToken cancellationToken = default);
    void Remove(string key);
}

/// <summary>Process-local cache with one in-flight value factory per cache key.</summary>
public sealed class ApplicationCache(IMemoryCache cache) : IApplicationCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> inFlight = new();

    public async Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, DateTimeOffset absoluteExpiration, CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue<T>(key, out var cached)) return cached!;

        var created = new Lazy<Task<object?>>(() => PopulateAsync(key, factory, absoluteExpiration));
        var pending = inFlight.GetOrAdd(key, created);
        if (ReferenceEquals(created, pending))
        {
            _ = pending.Value.ContinueWith(
                _ => ((ICollection<KeyValuePair<string, Lazy<Task<object?>>>>)inFlight).Remove(new(key, pending)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return (T)(await pending.Value.WaitAsync(cancellationToken))!;
    }

    public void Remove(string key) => cache.Remove(key);

    private async Task<object?> PopulateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, DateTimeOffset absoluteExpiration)
    {
        if (cache.TryGetValue<T>(key, out var cached)) return cached;

        var value = await factory(CancellationToken.None);
        if (value is not null) cache.Set(key, value, absoluteExpiration);
        return value;
    }
}
