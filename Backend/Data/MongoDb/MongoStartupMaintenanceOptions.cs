namespace Rankoon.Data.MongoDb;

public sealed class MongoStartupMaintenanceOptions
{
    public const string SectionName = "MongoStartupMaintenance";
    public int RepairBatchSize { get; set; } = 100;
}
