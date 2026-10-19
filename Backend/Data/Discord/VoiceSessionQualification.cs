using Rankoon.Data.Model;

namespace Rankoon.Data.Discord;

/// <summary>Tracks minimum-session qualification without losing eligible intervals.</summary>
internal sealed class VoiceSessionQualification
{
    internal long QualifyingSeconds { get; private set; }
    internal bool MinimumSessionSatisfied { get; private set; }
    internal List<VoiceEligibilityInterval> PendingIntervals { get; private set; } = [];

    internal static VoiceSessionQualification Prepare(VoiceSession session, long settingsRevision)
    {
        var state = new VoiceSessionQualification
        {
            QualifyingSeconds = session.QualifyingSeconds > 0 || session.EligibleSeconds > 0 ? session.QualifyingSeconds :
                Math.Max(0, (long)(session.LastAccruedAt - (session.EligibilityStartedAt ?? session.JoinedAt)).TotalSeconds),
            MinimumSessionSatisfied = session.MinimumSessionSatisfied ?? session.EligibleSeconds > 0,
            PendingIntervals = (session.PendingEligibilityIntervals ?? []).ToList()
        };
        if (session.EligibilitySettingsRevision.HasValue && session.EligibilitySettingsRevision != settingsRevision)
        {
            state.QualifyingSeconds = 0;
            state.MinimumSessionSatisfied = false;
            state.PendingIntervals = [];
        }
        else if (session.EligibilitySettingsRevision == null && session.EligibleSeconds == 0 && state.PendingIntervals.Count == 0 && state.QualifyingSeconds > 0)
            state.Append(session.EligibilityStartedAt ?? session.JoinedAt, session.LastAccruedAt);
        return state;
    }

    internal IReadOnlyList<VoiceEligibilityInterval> Advance(VoiceXpEligibilityResult result, VoiceXpEligibilitySettings settings, DateTime start, DateTime end)
    {
        if (end <= start) return [];
        if (!result.Qualifies)
        {
            if (settings.ResetMinimumSessionWhenIneligible)
            {
                QualifyingSeconds = 0;
                MinimumSessionSatisfied = false;
                PendingIntervals = [];
            }
            return [];
        }
        QualifyingSeconds += (long)(end - start).TotalSeconds;
        if (MinimumSessionSatisfied) return [new(start, end)];
        Append(start, end);
        if (!result.EligibleAfterMinimumDuration) return [];
        MinimumSessionSatisfied = true;
        var awarded = PendingIntervals;
        PendingIntervals = [];
        return awarded;
    }

    private void Append(DateTime start, DateTime end)
    {
        if (PendingIntervals.Count > 0 && PendingIntervals[^1].EndsAtUtc == start)
            PendingIntervals[^1] = PendingIntervals[^1] with { EndsAtUtc = end };
        else PendingIntervals.Add(new(start, end));
    }
}
