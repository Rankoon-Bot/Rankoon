namespace Rankoon.Data.Xp;

// Fixed stripes bound memory while serializing imports, transitions and repair within a process.
internal static class LevelRoleSynchronizationLock
{
    private static readonly SemaphoreSlim[] Stripes = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly SemaphoreSlim[] SeasonStripes = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    internal static async Task<IAsyncDisposable> AcquireSeasonAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var gate = SeasonStripes[(int)(guildId % (ulong)SeasonStripes.Length)];
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    internal static async Task<IAsyncDisposable> AcquireAsync(ulong guildId, ulong userId, CancellationToken cancellationToken)
    {
        var gate = Stripes[(int)((guildId ^ userId) % (ulong)Stripes.Length)];
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
    }
}
