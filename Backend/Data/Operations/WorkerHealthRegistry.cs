using System.Collections.Concurrent;

namespace Rankoon.Data.Operations;

public enum WorkerHealthState
{
    Healthy,
    Degraded,
    Unhealthy
}

public sealed record WorkerHealthSnapshot(string Worker, WorkerHealthState State, DateTimeOffset ReportedAt, string Detail, bool IsStale);

public interface IWorkerHealthRegistry
{
    void Report(string worker, WorkerHealthState state, string? detail = null);
    bool Remove(string worker);
    IReadOnlyList<WorkerHealthSnapshot> GetSnapshot(TimeSpan staleAfter);
}

public sealed class WorkerHealthRegistry(TimeProvider timeProvider) : IWorkerHealthRegistry
{
    private readonly ConcurrentDictionary<string, Entry> _workers = new(StringComparer.Ordinal);

    public void Report(string worker, WorkerHealthState state, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        _workers[worker.Trim()] = new(state, timeProvider.GetUtcNow(), Bound(detail, 500));
    }

    public bool Remove(string worker) => !string.IsNullOrWhiteSpace(worker) && _workers.TryRemove(worker.Trim(), out _);

    public IReadOnlyList<WorkerHealthSnapshot> GetSnapshot(TimeSpan staleAfter)
    {
        if (staleAfter < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(staleAfter));
        var now = timeProvider.GetUtcNow();
        return _workers.Select(pair => new WorkerHealthSnapshot(pair.Key, pair.Value.State, pair.Value.ReportedAt,
                pair.Value.Detail, now - pair.Value.ReportedAt > staleAfter))
            .OrderBy(item => item.Worker, StringComparer.Ordinal)
            .ToArray();
    }

    private static string Bound(string? value, int maxLength) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];

    private sealed record Entry(WorkerHealthState State, DateTimeOffset ReportedAt, string Detail);
}
