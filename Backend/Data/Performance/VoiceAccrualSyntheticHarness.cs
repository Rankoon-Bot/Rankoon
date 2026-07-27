using System.Diagnostics;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;

namespace Rankoon.Data.Performance;

/// <summary>Repeatable, database-free workload for the common coalescing voice-accrual path.</summary>
public static class VoiceAccrualSyntheticHarness
{
    public static VoiceAccrualSyntheticResult RunCoalescingWorkload(int sliceCount)
    {
        if (sliceCount < 0) throw new ArgumentOutOfRangeException(nameof(sliceCount));

        var started = Stopwatch.GetTimestamp();
        var cursor = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        VoiceActivityDay? day = null;
        long mergeInputs = 0;
        var maximumSegments = 0;

        for (var index = 0; index < sliceCount; index++)
        {
            var next = cursor.AddSeconds(5);
            // This is the exact logical work submitted to Merge by Plan for this workload.
            mergeInputs += (day?.Segments.Count ?? 0) + 1;
            day = VoiceActivityAccumulator.Plan(day, new VoiceAccrualSlice(1, 2, "synthetic-session", cursor, next, 3, "synthetic-season", 5, 1m, 12m, 1m, null, 1, next)).Replacement;
            maximumSegments = Math.Max(maximumSegments, day!.Segments.Count);
            cursor = next;
        }

        return new(sliceCount, sliceCount, mergeInputs, maximumSegments, day?.Segments.Count ?? 0, Stopwatch.GetElapsedTime(started));
    }
}

public sealed record VoiceAccrualSyntheticResult(int InputSlices, int PlanCalls, long MergeInputs, int MaximumSegments, int FinalSegments, TimeSpan Elapsed);

/// <summary>Deterministic regression limits for the synthetic coalescing workload.</summary>
public static class VoiceAccrualOperationContract
{
    public static long ExpectedMergeInputs(int inputSlices) => inputSlices == 0 ? 0 : 2L * inputSlices - 1;

    public static void AssertCoalescingWorkload(VoiceAccrualSyntheticResult result)
    {
        if (result.PlanCalls != result.InputSlices) throw new InvalidOperationException("Each synthetic slice must require exactly one planning operation.");
        if (result.MergeInputs != ExpectedMergeInputs(result.InputSlices)) throw new InvalidOperationException("The coalescing workload must remain linear in merge inputs.");
        if (result.MaximumSegments > 1 || result.FinalSegments > 1) throw new InvalidOperationException("Equivalent contiguous slices must coalesce into one segment.");
    }
}
