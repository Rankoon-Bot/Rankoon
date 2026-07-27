using Rankoon.Data.MongoDb;
using Xunit;

namespace Backend.Tests;

public sealed class MongoStartupMaintenanceOptionsTests
{
    [Fact]
    public void Startup_repairs_default_to_a_small_bounded_batch()
    {
        var options = new MongoStartupMaintenanceOptions();

        Assert.Equal(MongoStartupMaintenanceOptions.SectionName, "MongoStartupMaintenance");
        Assert.InRange(options.RepairBatchSize, 1, 1000);
        Assert.Equal(100, options.RepairBatchSize);
    }
}
