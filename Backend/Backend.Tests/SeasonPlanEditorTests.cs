using MongoDB.Bson;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;
namespace Rankoon.Backend.Tests;
public sealed class SeasonPlanEditorTests
{
    private readonly DateTime now = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static GuildSeasonSettings Config() => new() { GuildId = 1, Enabled = true, TimeZoneId = "UTC", ScheduleKind = SeasonScheduleKind.FixedDuration, FixedDurationDays = 7, ScheduleAnchorUtc = new(2030, 2, 1, 0, 0, 0, DateTimeKind.Utc) };
    private static List<GuildSeason> Plan() => Enumerable.Range(0, 3).Select(i => new GuildSeason { Id = ObjectId.GenerateNewId().ToString(), GuildId = 1, Sequence = i + 1, Number = i + 1, Name = $"Season {i + 1}", AutomaticallyNamed = true, SettingsSnapshot = Config(), StartsAtUtc = Config().ScheduleAnchorUtc!.Value.AddDays(i * 7), EndsAtUtc = Config().ScheduleAnchorUtc!.Value.AddDays((i + 1) * 7) }).ToList();
    [Fact] public void Delete_keeps_dates_and_renumbers_without_cancelled_artifacts()
    {
        var plan = Plan(); var result = SeasonPlanEditor.Preview(Config(), plan, new("Delete", plan[1].Id), now);
        Assert.Null(result.Changes.Single(r => r.Before.Id == plan[1].Id).After);
        var last = result.Changes.Single(r => r.Before.Id == plan[2].Id).After!;
        Assert.Equal(plan[2].StartsAtUtc, last.StartsAtUtc); Assert.Equal("Season 2", last.Name); Assert.Equal(2, last.Number);
        Assert.Equal("Season 3", plan[2].Name); // Preview must not mutate its source.
    }
    [Fact] public void Delete_can_close_the_gap()
    {
        var plan = Plan(); var result = SeasonPlanEditor.Preview(Config(), plan, new("Delete", plan[1].Id, "Shift"), now);
        var last = result.Changes.Single(r => r.Before.Id == plan[2].Id).After!;
        Assert.Equal(plan[1].StartsAtUtc, last.StartsAtUtc); Assert.Equal(plan[1].EndsAtUtc, last.EndsAtUtc);
    }
    [Fact] public void Delete_tail_does_not_delete_live_history_or_reserved_breaks()
    {
        var plan = Plan(); plan[2].Status = SeasonStatus.Paused;
        var result = SeasonPlanEditor.Preview(Config(), plan, new("Delete", plan[0].Id, "Delete"), now);
        Assert.Equal(2, result.Changes.Count(r => r.After == null)); Assert.DoesNotContain(result.Changes, r => r.Before.Id == plan[2].Id);
    }
    [Fact] public void Edit_rejects_overlap_and_can_shift_following_periods()
    {
        var plan = Plan(); var request = new SeasonPlanChangeRequest("Update", plan[0].Id, Name: "Spring", StartsAtUtc: plan[0].StartsAtUtc, EndsAtUtc: plan[0].EndsAtUtc.AddDays(2));
        Assert.Throws<SeasonPlanningConflictException>(() => SeasonPlanEditor.Preview(Config(), plan, request, now));
        var result = SeasonPlanEditor.Preview(Config(), plan, request with { FollowUp = "Shift" }, now);
        Assert.Equal(plan[1].StartsAtUtc.AddDays(2), result.Changes.Single(r => r.Before.Id == plan[1].Id).After!.StartsAtUtc);
    }
    [Fact] public void Break_is_reserved_and_does_not_consume_a_season_number()
    {
        var plan = Plan(); var result = SeasonPlanEditor.Preview(Config(), plan, new("Pause", plan[1].Id, Name: "Sommerpause"), now);
        var paused = result.Changes.Single(r => r.Before.Id == plan[1].Id).After!;
        Assert.Equal(SeasonStatus.Paused, paused.Status); Assert.Equal("Sommerpause", paused.PauseLabel);
        Assert.Equal(plan[1].StartsAtUtc, paused.StartsAtUtc); Assert.Equal("Season 2", result.Changes.Single(r => r.Before.Id == plan[2].Id).After!.Name);
        Assert.DoesNotContain(SeasonSchedulePlanner.GenerateAdditional(Config(), [plan[0], paused], 3, now), s => s.StartsAtUtc < paused.EndsAtUtc && paused.StartsAtUtc < s.EndsAtUtc);
    }
    [Fact] public void Break_can_be_restored_before_its_end()
    {
        var plan = Plan(); plan[1].Status = SeasonStatus.Paused;
        Assert.Equal(SeasonStatus.Scheduled, SeasonPlanEditor.Preview(Config(), plan, new("ResumePause", plan[1].Id), now).Changes.Single().After!.Status);
        Assert.Throws<SeasonPlanningConflictException>(() => SeasonPlanEditor.Preview(Config(), plan, new("ResumePause", plan[1].Id), plan[1].EndsAtUtc));
    }
    [Theory] [InlineData(SeasonStatus.Active)] [InlineData(SeasonStatus.Closing)] [InlineData(SeasonStatus.Closed)]
    public void Running_and_historical_seasons_are_protected(SeasonStatus status)
    {
        var plan = Plan(); plan[0].Status = status;
        Assert.Throws<SeasonPlanningConflictException>(() => SeasonPlanEditor.Preview(Config(), plan, new("Delete", plan[0].Id), now));
    }
    [Fact] public void Initialized_cancelled_seasons_are_not_drafts()
    {
        var plan = Plan(); plan[0].Status = SeasonStatus.Cancelled; plan[0].BaselineInitialized = true;
        Assert.Throws<SeasonPlanningConflictException>(() => SeasonPlanEditor.Preview(Config(), plan, new("Delete", plan[0].Id), now));
    }
    [Fact] public void Shift_preserves_local_time_across_daylight_saving_change()
    {
        var cfg = Config(); cfg.TimeZoneId = "Europe/Berlin"; var plan = Plan();
        plan[0].StartsAtUtc = new(2030, 3, 28, 9, 0, 0, DateTimeKind.Utc); plan[0].EndsAtUtc = new(2030, 3, 29, 9, 0, 0, DateTimeKind.Utc);
        plan[1].StartsAtUtc = new(2030, 4, 1, 8, 0, 0, DateTimeKind.Utc); plan[1].EndsAtUtc = new(2030, 4, 2, 8, 0, 0, DateTimeKind.Utc);
        var result = SeasonPlanEditor.Preview(cfg, plan, new("Delete", plan[0].Id, "Shift"), now);
        Assert.Equal(plan[0].StartsAtUtc, result.Changes.Single(r => r.Before.Id == plan[1].Id).After!.StartsAtUtc);
    }
}
