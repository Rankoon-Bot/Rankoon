namespace Rankoon.Data.Xp;

internal sealed record XpBatchFailure<T>(T Item, Exception Error);

/// <summary>A failed member or ledger entry must not prevent unrelated awards.</summary>
internal static class XpBatchProcessor
{
    internal static async Task<IReadOnlyList<XpBatchFailure<T>>> RunAsync<T>(IEnumerable<T> items, Func<T, Task> process, CancellationToken cancellationToken)
    {
        var failures = new List<XpBatchFailure<T>>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await process(item); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { failures.Add(new(item, exception)); }
        }
        return failures;
    }
}
