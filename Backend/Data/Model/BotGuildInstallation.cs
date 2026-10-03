using MongoDB.Bson.Serialization.Attributes;

namespace Rankoon.Data.Model;

/// <summary>One Discord membership, retained after removal and across runtime restarts.</summary>
public sealed class BotGuildInstallation
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public ulong BotId { get; set; }
    public ulong GuildId { get; set; }
    public string GuildName { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public DateTime JoinedAtUtc { get; set; }
    public DateTime FirstObservedAtUtc { get; set; }
    public DateTime LastObservedAtUtc { get; set; }
    public DateTime HistoryAtUtc { get; set; }
    public bool JoinObserved { get; set; }
    public DateTime? RemovedAtUtc { get; set; }
    public string? RemovalEvidence { get; set; }
}
