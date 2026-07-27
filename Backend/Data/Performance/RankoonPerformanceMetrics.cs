using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Rankoon.Data.Performance;

/// <summary>OpenTelemetry-compatible measurements for bounded, high-volume Rankoon operations.</summary>
public static class RankoonPerformanceMetrics
{
    public const string InstrumentationName = "Rankoon.Performance";
    public static readonly ActivitySource ActivitySource = new(InstrumentationName, "1.0.0");
    public static readonly Meter Meter = new(InstrumentationName, "1.0.0");

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("rankoon.operation.duration", "ms", "Elapsed operation duration.");
    private static readonly Counter<long> Completed = Meter.CreateCounter<long>("rankoon.operation.count", "{operation}", "Completed operations.");
    private static readonly Counter<long> DatabaseOperations = Meter.CreateCounter<long>("rankoon.database.operation.count", "{operation}", "MongoDB operations issued by an instrumented operation.");

    public static RankoonPerformanceOperation Start(string operation, string component) => new(operation, component);

    public sealed class RankoonPerformanceOperation : IDisposable
    {
        private readonly string operation;
        private readonly string component;
        private readonly Activity? activity;
        private readonly long started = Stopwatch.GetTimestamp();
        private string outcome = "error";
        private long databaseOperations;
        private bool disposed;

        internal RankoonPerformanceOperation(string operation, string component)
        {
            this.operation = operation;
            this.component = component;
            activity = ActivitySource.StartActivity(operation, ActivityKind.Internal);
            activity?.SetTag("rankoon.component", component);
        }

        public void AddDatabaseOperation() => databaseOperations++;

        public void Complete(string operationOutcome = "success")
        {
            outcome = operationOutcome;
            activity?.SetStatus(ActivityStatusCode.Ok);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var tags = new TagList { { "rankoon.operation", operation }, { "rankoon.component", component }, { "outcome", outcome } };
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
            Completed.Add(1, tags);
            if (databaseOperations > 0) DatabaseOperations.Add(databaseOperations, tags);
            activity?.SetTag("rankoon.database.operation.count", databaseOperations);
            activity?.Dispose();
        }
    }
}
