using System.Diagnostics;
using System.Diagnostics.Metrics;
using Rankoon.Data.Performance;
using Xunit;

namespace Backend.Tests;

public sealed class PerformanceTelemetryTests
{
    [Fact]
    public void Performance_operation_emits_activity_duration_completion_and_database_metrics()
    {
        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RankoonPerformanceMetrics.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(activity)
        };
        var measurements = new List<(string Name, long Value)>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == RankoonPerformanceMetrics.InstrumentationName) listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, _, _) => measurements.Add((instrument.Name, value)));
        meterListener.Start();

        using (var operation = RankoonPerformanceMetrics.Start("test.operation", "test-component"))
        {
            operation.AddDatabaseOperation();
            operation.AddDatabaseOperation();
            operation.Complete("applied");
        }

        var activity = Assert.Single(activities);
        Assert.Equal("test.operation", activity.OperationName);
        Assert.Equal("test-component", activity.GetTagItem("rankoon.component"));
        Assert.Equal(2L, activity.GetTagItem("rankoon.database.operation.count"));
        Assert.Contains(measurements, measurement => measurement == ("rankoon.operation.count", 1));
        Assert.Contains(measurements, measurement => measurement == ("rankoon.database.operation.count", 2));
    }

    [Fact]
    public void Coalescing_synthetic_workload_meets_the_operation_count_contract()
    {
        var result = VoiceAccrualSyntheticHarness.RunCoalescingWorkload(20_000);

        VoiceAccrualOperationContract.AssertCoalescingWorkload(result);
        Assert.Equal(20_000, result.PlanCalls);
        Assert.Equal(39_999, result.MergeInputs);
        Assert.Equal(1, result.FinalSegments);
    }
}
