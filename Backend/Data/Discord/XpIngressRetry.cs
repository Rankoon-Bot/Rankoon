using MongoDB.Driver;

namespace Rankoon.Data.Discord;

internal static class XpIngressRetry
{
    // Gateway events are not replayed after a handler fails. Retry short outages
    // here; the immutable grant key makes retries after a successful insert safe.
    internal static async Task RunAsync(Func<Task> process, TimeProvider timeProvider, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await process(); return; }
            catch (Exception exception) when (attempt < 2 && IsTransient(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(attempt == 0 ? 100 : 300), timeProvider, cancellationToken);
            }
        }
    }

    private static bool IsTransient(Exception exception) => exception is TimeoutException or MongoConnectionException or MongoExecutionTimeoutException ||
        exception is MongoException mongo && (mongo.HasErrorLabel("RetryableWriteError") || mongo.HasErrorLabel("TransientTransactionError"));
}
