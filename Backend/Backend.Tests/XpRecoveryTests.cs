using Rankoon.Data.Discord;
using Discord;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class XpRecoveryTests
{
    [Theory]
    [InlineData(ConnectionState.Connected, true, true)]
    [InlineData(ConnectionState.Connected, false, false)]
    [InlineData(ConnectionState.Disconnected, true, false)]
    [InlineData(ConnectionState.Connecting, true, false)]
    [InlineData(ConnectionState.Disconnecting, true, false)]
    public void Disconnected_or_unavailable_guilds_cannot_award_from_stale_voice_cache(ConnectionState state, bool available, bool expected) =>
        Assert.Equal(expected, VoiceXpWatchdog.CanObserveVoice(state, available));

    [Fact]
    public void Persisted_voice_sessions_require_the_same_observation_epoch_to_resume()
    {
        var session = new VoiceSession { ChannelId = 3, SessionId = "session", RuntimeBootId = "connected" };
        Assert.True(VoiceXpWatchdog.CanResumeSession(session, 3, "connected"));
        Assert.False(VoiceXpWatchdog.CanResumeSession(session, 4, "connected"));
        Assert.False(VoiceXpWatchdog.CanResumeSession(session, 3, "reconnected"));
        session.RuntimeBootId = null;
        Assert.False(VoiceXpWatchdog.CanResumeSession(session, 3, "connected"));
        session.RuntimeBootId = "connected"; session.SessionId = "";
        Assert.False(VoiceXpWatchdog.CanResumeSession(session, 3, "connected"));
    }
    [Fact]
    public async Task A_bad_member_or_ledger_entry_does_not_block_the_rest_of_a_batch()
    {
        var processed = new List<int>();
        var error = new InvalidOperationException("Bad entry");
        var failures = await XpBatchProcessor.RunAsync([1, 2, 3, 4], item =>
        {
            if (item is 1 or 3) throw error;
            processed.Add(item);
            return Task.CompletedTask;
        }, default);
        Assert.Equal([2, 4], processed);
        Assert.Equal([1, 3], failures.Select(x => x.Item));
        Assert.All(failures, failure => Assert.Same(error, failure.Error));
    }

    [Fact]
    public async Task Batch_shutdown_cancellation_stops_before_the_next_member()
    {
        using var cancelled = new CancellationTokenSource();
        var processed = new List<int>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => XpBatchProcessor.RunAsync([1, 2], item =>
        {
            processed.Add(item); cancelled.Cancel();
            return Task.FromCanceled(cancelled.Token);
        }, cancelled.Token));
        Assert.Equal([1], processed);
    }

    [Fact]
    public async Task Gateway_transient_failure_is_retried_with_a_fixed_upper_bound()
    {
        var attempts = 0;
        await XpIngressRetry.RunAsync(() => ++attempts < 3 ? Task.FromException(new TimeoutException()) : Task.CompletedTask, TimeProvider.System);
        Assert.Equal(3, attempts);
        attempts = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => XpIngressRetry.RunAsync(() =>
        {
            attempts++; throw new TimeoutException();
        }, TimeProvider.System));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Gateway_validation_errors_are_not_retried()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => XpIngressRetry.RunAsync(() =>
        {
            attempts++; throw new ArgumentException();
        }, TimeProvider.System));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Gateway_cancellation_does_not_start_an_award()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => XpIngressRetry.RunAsync(() =>
        {
            attempts++; return Task.CompletedTask;
        }, TimeProvider.System, cancelled.Token));
        Assert.Equal(0, attempts);
    }

    [Fact]
    public void Minimum_voice_duration_releases_all_qualifying_time_once_at_the_boundary()
    {
        var progress = VoiceSessionQualification.Prepare(Session(), 1);
        Assert.Empty(Advance(progress, 0, 30));
        Assert.Equal(30, progress.QualifyingSeconds);
        var awarded = Assert.Single(Advance(progress, 30, 60));
        Assert.Equal(DateTime.UnixEpoch, awarded.StartsAtUtc);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(60), awarded.EndsAtUtc);
        Assert.Empty(progress.PendingIntervals);
        var next = Assert.Single(Advance(progress, 60, 90));
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(60), next.StartsAtUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ineligible_gaps_are_never_awarded_and_reset_policy_is_respected(bool reset)
    {
        var progress = VoiceSessionQualification.Prepare(Session(), 1);
        var settings = new VoiceXpEligibilitySettings { ResetMinimumSessionWhenIneligible = reset };
        Assert.Empty(Advance(progress, 0, 30, settings: settings));
        Assert.Empty(Advance(progress, 30, 60, eligible: false, settings: settings));
        var next = Advance(progress, 60, 90, settings: settings);
        if (reset)
        {
            Assert.Empty(next);
            Assert.Equal(30, progress.QualifyingSeconds);
            Assert.Equal(DateTime.UnixEpoch.AddSeconds(60), Assert.Single(progress.PendingIntervals).StartsAtUtc);
        }
        else
        {
            Assert.Equal(2, next.Count);
            Assert.Equal(60, next.Sum(x => (x.EndsAtUtc - x.StartsAtUtc).TotalSeconds));
            Assert.DoesNotContain(next, x => x.StartsAtUtc < DateTime.UnixEpoch.AddSeconds(60) && x.EndsAtUtc > DateTime.UnixEpoch.AddSeconds(30));
        }
    }

    [Fact]
    public void Settings_revision_change_resets_qualification_without_mutating_the_persisted_session()
    {
        var session = Session();
        session.QualifyingSeconds = 30;
        session.PendingEligibilityIntervals = [new(DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(30))];
        var state = VoiceSessionQualification.Prepare(session, 2);
        Assert.Equal(0, state.QualifyingSeconds);
        Assert.Empty(state.PendingIntervals);
        Assert.Equal(30, session.QualifyingSeconds);
        Assert.Single(session.PendingEligibilityIntervals);
    }

    [Fact]
    public void Legacy_session_recovers_pending_time_and_already_qualified_sessions_do_not_wait_again()
    {
        var session = Session();
        session.EligibilitySettingsRevision = null;
        session.LastAccruedAt = DateTime.UnixEpoch.AddSeconds(30);
        var state = VoiceSessionQualification.Prepare(session, 1);
        Assert.Equal(30, state.QualifyingSeconds);
        Assert.Equal(60, (Assert.Single(Advance(state, 30, 60)).EndsAtUtc - DateTime.UnixEpoch).TotalSeconds);
        session.EligibleSeconds = 60;
        session.MinimumSessionSatisfied = null;
        state = VoiceSessionQualification.Prepare(session, 1);
        Assert.True(state.MinimumSessionSatisfied);
        Assert.Single(Advance(state, 60, 65));
    }

    [Fact]
    public void Voice_migration_keeps_the_original_award_precision()
    {
        var slice = new VoiceAccrualSlice(1, 2, "legacy", DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(7), 3, null, 7,
            1.23456789m, 1.23456789m * 60 / 7, 1, null, VoiceLedgerMigrationService.LegacySettingsRevision, DateTime.UnixEpoch, AlreadyProjected: true);
        var day = VoiceActivityAccumulator.Plan(null, slice).Replacement!;
        Assert.Equal(slice.AwardedXp, day.TotalAwardedXp);
        Assert.Equal(slice.AwardedXp, day.ProjectedXp);
        Assert.Equal(VoiceActivityProjectionStatus.Applied, day.ProjectionStatus);
    }

    [Theory]
    [InlineData("negativeXp")]
    [InlineData("negativeRate")]
    [InlineData("secondsExceedDuration")]
    [InlineData("dayBoundary")]
    [InlineData("emptySession")]
    [InlineData("member")]
    public void Invalid_voice_slices_are_rejected_before_planning(string invalid)
    {
        var slice = new VoiceAccrualSlice(1, 2, "session", DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(60), 3, null, 60, 10, 10, 1, null, 1, DateTime.UnixEpoch);
        slice = invalid switch
        {
            "negativeXp" => slice with { AwardedXp = -1 },
            "negativeRate" => slice with { EffectiveXpPerMinute = -1 },
            "secondsExceedDuration" => slice with { EligibleSeconds = 61 },
            "dayBoundary" => slice with { EndsAtUtc = DateTime.UnixEpoch.AddDays(1).AddSeconds(1) },
            "emptySession" => slice with { SessionId = "" },
            _ => slice with { UserId = 0 }
        };
        Assert.ThrowsAny<ArgumentException>(() => VoiceActivityAccumulator.Plan(null, slice));
    }

    private static VoiceSession Session() => new() { JoinedAt = DateTime.UnixEpoch, LastAccruedAt = DateTime.UnixEpoch, EligibilitySettingsRevision = 1 };
    private static IReadOnlyList<VoiceEligibilityInterval> Advance(VoiceSessionQualification state, int from, int to, bool eligible = true, VoiceXpEligibilitySettings? settings = null)
    {
        settings ??= new();
        var participant = new VoiceXpParticipantState(1, HasExcludedRole: !eligible);
        var result = new VoiceXpEligibilityEvaluator().Evaluate(new(participant, [participant, new(2)], QualifyingSeconds: state.QualifyingSeconds, CurrentIntervalSeconds: to - from, MinimumSessionSeconds: 60), settings);
        return state.Advance(result, settings, DateTime.UnixEpoch.AddSeconds(from), DateTime.UnixEpoch.AddSeconds(to));
    }
}
