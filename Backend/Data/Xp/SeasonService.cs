using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Xp;

public interface ISeasonService
{
    Task<GuildSeasonSettings> GetSettingsAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(GuildSeasonSettings settings, CancellationToken cancellationToken = default);
    Task<GuildSeason?> ResolveAsync(ulong guildId, DateTime occurredAtUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuildSeason>> GetSeasonsAsync(ulong guildId, CancellationToken cancellationToken = default);
}

/// <summary>Resolves persisted season instances only. XP sources must never infer a season from mutable settings.</summary>
public sealed class SeasonService(RankoonDbContext database, TimeProvider timeProvider) : ISeasonService
{
    public async Task<GuildSeasonSettings> GetSettingsAsync(ulong guildId, CancellationToken cancellationToken = default) =>
        await database.GuildSeasonSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken)
        ?? new GuildSeasonSettings { GuildId = guildId, UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime };

    public Task SaveSettingsAsync(GuildSeasonSettings settings, CancellationToken cancellationToken = default)
    {
        SeasonScheduleGenerator.Validate(settings);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var update = Builders<GuildSeasonSettings>.Update
            .SetOnInsert(x => x.GuildId, settings.GuildId)
            .Set(x => x.Enabled, settings.Enabled)
            .Set(x => x.DefaultLeaderboardScope, settings.DefaultLeaderboardScope)
            .Set(x => x.TimeZoneId, settings.TimeZoneId)
            .Set(x => x.ScheduleKind, settings.ScheduleKind)
            .Set(x => x.ScheduleAnchorUtc, settings.ScheduleAnchorUtc)
            .Set(x => x.FixedDurationDays, settings.FixedDurationDays)
            .Set(x => x.GapDays, settings.GapDays)
            .Set(x => x.PreparedSeasonCount, settings.PreparedSeasonCount)
            .Set(x => x.PauseBehavior, settings.PauseBehavior)
            .Set(x => x.PublicHistoryCount, settings.PublicHistoryCount)
            .Set(x => x.InitialXpMode, settings.InitialXpMode)
            .Set(x => x.InitialXpPercentage, settings.InitialXpPercentage)
            .Set(x => x.CarryOverMode, settings.CarryOverMode)
            .Set(x => x.CarryOverPercentage, settings.CarryOverPercentage)
            .Set(x => x.CarryOverMaximumXp, settings.CarryOverMaximumXp)
            .Set(x => x.AnnouncementChannelId, settings.AnnouncementChannelId)
            .Set(x => x.Announcements, settings.Announcements)
            .Set(x => x.WinnerCount, settings.WinnerCount)
            .Set(x => x.NameTemplate, settings.NameTemplate)
            .Set(x => x.Rotation, settings.Rotation)
            .Set(x => x.RotationOffset, settings.RotationOffset)
            .Set(x => x.SeasonLevelRoles, settings.SeasonLevelRoles)
            .Inc(x => x.Revision, 1)
            .Set(x => x.UpdatedAtUtc, now);
        return database.GuildSeasonSettings.UpdateOneAsync(x => x.GuildId == settings.GuildId, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    public async Task<GuildSeason?> ResolveAsync(ulong guildId, DateTime occurredAtUtc, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(guildId, cancellationToken);
        if (!settings.Enabled) return null;
        var instant = DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc);
        return await database.GuildSeasons.Find(x => x.GuildId == guildId && x.Status == SeasonStatus.Active && x.StartsAtUtc <= instant && instant < x.EndsAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GuildSeason>> GetSeasonsAsync(ulong guildId, CancellationToken cancellationToken = default) =>
        await database.GuildSeasons.Find(x => x.GuildId == guildId).SortByDescending(x => x.Sequence).ToListAsync(cancellationToken);
}
