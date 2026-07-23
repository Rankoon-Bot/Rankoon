using System.Collections.Concurrent;

namespace Rankoon.Data.Diagnostics;

public interface IDiagnosticReportCache
{
    Task<PermissionDiagnosticReport> GetOrCreateAsync(ulong guildId, PermissionDiagnosticScope scope, Func<CancellationToken, Task<PermissionDiagnosticReport>> factory, CancellationToken cancellationToken);
    PermissionDiagnosticReport? GetLatest(ulong guildId);
    void Invalidate(ulong guildId);
}

public sealed class DiagnosticReportCache(TimeProvider clock) : IDiagnosticReportCache
{
    private readonly ConcurrentDictionary<(ulong GuildId, PermissionDiagnosticScope Scope), Entry> entries = new();
    private readonly ConcurrentDictionary<ulong, PermissionDiagnosticReport> latest = new();
    private readonly ConcurrentDictionary<(ulong GuildId, PermissionDiagnosticScope Scope), Lazy<Task<PermissionDiagnosticReport>>> running = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(45);

    public async Task<PermissionDiagnosticReport> GetOrCreateAsync(ulong guildId, PermissionDiagnosticScope scope, Func<CancellationToken, Task<PermissionDiagnosticReport>> factory, CancellationToken cancellationToken)
    {
        if (entries.TryGetValue((guildId, scope), out var cached) && clock.GetUtcNow() - cached.CreatedAt < Lifetime) return cached.Report;
        var key = (guildId, scope);
        var scan = running.GetOrAdd(key, _ => new Lazy<Task<PermissionDiagnosticReport>>(() => factory(cancellationToken)));
        try
        {
            var report = await scan.Value;
            entries[key] = new(report, clock.GetUtcNow());
            latest[guildId] = report;
            return report;
        }
        finally { running.TryRemove(key, out _); }
    }
    public PermissionDiagnosticReport? GetLatest(ulong guildId) => latest.TryGetValue(guildId, out var report) ? report : null;
    public void Invalidate(ulong guildId) { foreach (var key in entries.Keys.Where(key => key.GuildId == guildId)) entries.TryRemove(key, out _); latest.TryRemove(guildId, out _); }
    private sealed record Entry(PermissionDiagnosticReport Report, DateTimeOffset CreatedAt);
}
