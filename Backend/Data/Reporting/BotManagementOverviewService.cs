using Discord.WebSocket;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Reporting;

public enum BotManagementRange { Last24Hours, Last7Days, Last30Days, Last90Days }
public enum BotManagementGuildStatus { VeryActive, Active, LowActivity, Inactive, New, AttentionRequired }

public sealed record BotManagementRangeDto(string Key, DateTimeOffset From, DateTimeOffset To);
public sealed record BotManagementSummary(long ConnectedGuildCount, long SummedMemberCount, long ActiveGuildCount, long ActivityEventCount, long CommandEventCount, long ErrorEventCount);
public sealed record BotManagementGuildDto(string GuildId, string Name, string? IconUrl, int MemberCount, DateTimeOffset? BotJoinedAt, DateTimeOffset? LastActivityAt, long ActivityEventCount, long CommandEventCount, long ErrorEventCount, long FailedEventCount, long UniqueActorCount, long ActiveDayCount, decimal ActivityPerHundredMembers, BotManagementGuildStatus Status);
public sealed record BotManagementOverviewResponse(DateTimeOffset GeneratedAt, BotManagementRangeDto Range, BotManagementSummary Summary, IReadOnlyList<BotManagementGuildDto> Guilds);

public interface IBotManagementOverviewService { Task<BotManagementOverviewResponse> GetOverviewAsync(BotManagementRange range, CancellationToken cancellationToken = default); }

public sealed class BotManagementOverviewService(RankoonDbContext database, DiscordShardedClient discord, TimeProvider timeProvider) : IBotManagementOverviewService
{
    public async Task<BotManagementOverviewResponse> GetOverviewAsync(BotManagementRange range, CancellationToken cancellationToken = default)
    {
        var to = timeProvider.GetUtcNow();
        var from = to - RangeDuration(range);
        var usageFrom = from < to.AddDays(-30) ? from : to.AddDays(-30);
        var events = await database.ReportEvents.Find(Builders<ReportEvent>.Filter.Gte(x => x.OccurredAt, usageFrom.UtcDateTime) & Builders<ReportEvent>.Filter.Lte(x => x.OccurredAt, to.UtcDateTime) & Builders<ReportEvent>.Filter.In(x => x.Category, [ReportCategories.Activity, ReportCategories.Command, ReportCategories.Error])).ToListAsync(cancellationToken);
        var byGuild = events.GroupBy(x => x.GuildId).ToDictionary(x => x.Key);
        var guilds = discord.Guilds.Select(guild => BuildGuild(guild, byGuild.TryGetValue(guild.Id, out var reports) ? reports.ToArray() : Array.Empty<ReportEvent>(), from, to)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(to, new(RangeKey(range), from, to), new(guilds.Length, guilds.Sum(x => (long)x.MemberCount), guilds.Count(x => x.ActivityEventCount + x.CommandEventCount > 0), guilds.Sum(x => x.ActivityEventCount), guilds.Sum(x => x.CommandEventCount), guilds.Sum(x => x.ErrorEventCount)), guilds);
    }

    private static BotManagementGuildDto BuildGuild(SocketGuild guild, IReadOnlyCollection<ReportEvent> events, DateTimeOffset from, DateTimeOffset to)
    {
        var period = events.Where(x => x.OccurredAt >= from.UtcDateTime && x.OccurredAt <= to.UtcDateTime).ToArray();
        var activity = period.LongCount(x => x.Category == ReportCategories.Activity);
        var commands = period.LongCount(x => x.Category == ReportCategories.Command);
        var errors = period.LongCount(x => x.Category == ReportCategories.Error);
        var failed = period.LongCount(x => x.Outcome == ReportOutcomes.Failed);
        var usage = events.Where(x => x.Category is ReportCategories.Activity or ReportCategories.Command).ToArray();
        DateTimeOffset? last = usage.Length == 0 ? null : new DateTimeOffset(DateTime.SpecifyKind(usage.Max(x => x.OccurredAt), DateTimeKind.Utc));
        var activeDays = usage.Where(x => x.OccurredAt >= from.UtcDateTime && x.OccurredAt <= to.UtcDateTime).Select(x => DateOnly.FromDateTime(DateTime.SpecifyKind(x.OccurredAt, DateTimeKind.Utc))).Distinct().LongCount();
        var joined = guild.CurrentUser?.JoinedAt;
        var memberCount = guild.MemberCount;
        var intensity = memberCount <= 0 ? 0 : decimal.Round((activity + commands) * 100m / memberCount, 2);
        return new(guild.Id.ToString(), guild.Name, guild.IconUrl, memberCount, joined, last, activity, commands, errors, failed, period.Where(x => x.ActorId != null).Select(x => x.ActorId).Distinct().LongCount(), activeDays, intensity, CalculateStatus(joined, last, activity, commands, errors, failed, activeDays, from, to));
    }

    public static BotManagementGuildStatus CalculateStatus(DateTimeOffset? joinedAt, DateTimeOffset? lastActivityAt, long activity, long commands, long errors, long failed, long activeDays, DateTimeOffset from, DateTimeOffset to)
    {
        var total = activity + commands + errors;
        if (errors >= 10 || failed >= 10 && total > 0 && failed * 100m / total >= 20m) return BotManagementGuildStatus.AttentionRequired;
        if (joinedAt > to.AddDays(-7)) return BotManagementGuildStatus.New;
        var usage = activity + commands;
        if (usage > 0 && lastActivityAt >= to.AddHours(-24) && activeDays >= RequiredActiveDays(from, to)) return BotManagementGuildStatus.VeryActive;
        if (usage > 0) return BotManagementGuildStatus.Active;
        return lastActivityAt >= to.AddDays(-30) ? BotManagementGuildStatus.LowActivity : BotManagementGuildStatus.Inactive;
    }

    private static long RequiredActiveDays(DateTimeOffset from, DateTimeOffset to) => Math.Max(1, (long)Math.Ceiling(Math.Max(1, (to - from).TotalDays) / 2));
    public static TimeSpan RangeDuration(BotManagementRange range) => range switch { BotManagementRange.Last24Hours => TimeSpan.FromHours(24), BotManagementRange.Last7Days => TimeSpan.FromDays(7), BotManagementRange.Last30Days => TimeSpan.FromDays(30), BotManagementRange.Last90Days => TimeSpan.FromDays(90), _ => throw new ArgumentOutOfRangeException(nameof(range)) };
    public static string RangeKey(BotManagementRange range) => range switch { BotManagementRange.Last24Hours => "24h", BotManagementRange.Last7Days => "7d", BotManagementRange.Last30Days => "30d", BotManagementRange.Last90Days => "90d", _ => throw new ArgumentOutOfRangeException(nameof(range)) };
    public static bool TryParseRange(string? key, out BotManagementRange range) { range = key switch { "24h" => BotManagementRange.Last24Hours, "7d" or null or "" => BotManagementRange.Last7Days, "30d" => BotManagementRange.Last30Days, "90d" => BotManagementRange.Last90Days, _ => default }; return key is null or "" or "24h" or "7d" or "30d" or "90d"; }
}
