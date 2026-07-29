using Microsoft.AspNetCore.Mvc;
using Rankoon.Controllers;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Rankoon.Backend.Tests;

public sealed class SeasonLifecycleTests
{
    [Fact]
    public void Cancelled_deletion_order_is_leaf_to_root_and_excludes_chains_with_retained_successors()
    {
        var independentRoot = Season("000000000000000000000001", 1, SeasonStatus.Cancelled);
        var independentLeaf = Season("000000000000000000000002", 2, SeasonStatus.Cancelled, independentRoot.Id);
        var blockedRoot = Season("000000000000000000000003", 3, SeasonStatus.Cancelled);
        var blockedLeaf = Season("000000000000000000000004", 4, SeasonStatus.Cancelled, blockedRoot.Id);
        var activeSuccessor = Season("000000000000000000000005", 5, SeasonStatus.Active, blockedLeaf.Id);

        var result = SeasonLifecycleService.GetCancelledDeletionOrder([independentRoot, independentLeaf, blockedRoot, blockedLeaf, activeSuccessor]);

        Assert.Equal([independentLeaf.Id!, independentRoot.Id!], result);
    }

    [Theory]
    [InlineData(SeasonStatus.Scheduled)]
    [InlineData(SeasonStatus.Active)]
    [InlineData(SeasonStatus.Closing)]
    [InlineData(SeasonStatus.Closed)]
    public void Bulk_deletion_never_selects_non_cancelled_seasons(SeasonStatus status)
    {
        var season = Season("000000000000000000000001", 1, status);

        Assert.Empty(SeasonLifecycleService.GetCancelledDeletionOrder([season]));
    }

    [Fact]
    public void Bulk_endpoints_have_explicit_non_parameter_routes()
    {
        var cancel = typeof(SeasonController).GetMethod(nameof(SeasonController.CancelScheduled))!
            .GetCustomAttributes(typeof(HttpPostAttribute), false).Cast<HttpPostAttribute>().Single();
        var delete = typeof(SeasonController).GetMethod(nameof(SeasonController.DeleteCancelled))!
            .GetCustomAttributes(typeof(HttpDeleteAttribute), false).Cast<HttpDeleteAttribute>().Single();
        var reset = typeof(SeasonController).GetMethod(nameof(SeasonController.ResetCounter))!
            .GetCustomAttributes(typeof(HttpPostAttribute), false).Cast<HttpPostAttribute>().Single();

        Assert.Equal("cancel-scheduled", cancel.Template);
        Assert.Equal("cancelled", delete.Template);
        Assert.Equal("counter/reset", reset.Template);
    }

    private static GuildSeason Season(string id, long sequence, SeasonStatus status, string? previousSeasonId = null) => new()
    {
        Id = id,
        GuildId = 1,
        Sequence = sequence,
        Status = status,
        PreviousSeasonId = previousSeasonId
    };
}
