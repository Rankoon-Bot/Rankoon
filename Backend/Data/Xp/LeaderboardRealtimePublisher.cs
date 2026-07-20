using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Hubs;

namespace Rankoon.Data.Xp;

public sealed record LeaderboardEntryChanged(string Alias, SeasonLeaderboardScope Scope, string? SeasonId, string Operation, string UserId, LeaderboardEntryDto? Entry);
public sealed record LeaderboardSettingsChanged(string Alias, LeaderboardVisibility Visibility);

public interface ILeaderboardRealtimePublisher
{
    Task PublishMemberAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default);
    Task PublishGuildAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task PublishSettingsAsync(GuildLeaderboardSettings settings, string? previousAlias = null, CancellationToken cancellationToken = default);
}

public sealed class LeaderboardRealtimePublisher(RankoonDbContext database, IHubContext<LeaderboardHub> hub, LeaderboardSubscriptionRegistry subscriptions) : ILeaderboardRealtimePublisher
{
    public async Task PublishMemberAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default)
    {
        var settings = await database.GuildLeaderboardSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (settings == null) return;

        var member = await database.MemberXp.Find(x => x.GuildId == guildId && x.UserId == userId).FirstOrDefaultAsync(cancellationToken);
        if (member?.IsCurrentMember != true)
        {
            foreach (var subscription in subscriptions.RemoveMemberSubscriptions(guildId, userId))
            {
                await hub.Groups.RemoveFromGroupAsync(subscription.ConnectionId, subscription.Group, cancellationToken);
                await hub.Groups.RemoveFromGroupAsync(subscription.ConnectionId, subscription.AudienceGroup, cancellationToken);
                await hub.Clients.Client(subscription.ConnectionId).SendAsync("leaderboardAccessRevoked", new LeaderboardSettingsChanged(settings.Alias, settings.Visibility), cancellationToken);
            }
        }
        await PublishLifetimeAsync(settings, member, true, cancellationToken);
        await PublishLifetimeAsync(settings, member, false, cancellationToken);

        var activeSeason = await database.GuildSeasons.Find(x => x.GuildId == guildId && x.Status == SeasonStatus.Active).FirstOrDefaultAsync(cancellationToken);
        if (activeSeason != null)
        {
            var seasonMember = await database.SeasonMemberXp.Find(x => x.SeasonId == activeSeason.Id && x.UserId == userId).FirstOrDefaultAsync(cancellationToken);
            await PublishCurrentSeasonAsync(settings, activeSeason, seasonMember, true, cancellationToken);
            await PublishCurrentSeasonAsync(settings, activeSeason, seasonMember, false, cancellationToken);
        }

        var standings = await database.SeasonFinalStandings.Find(x => x.GuildId == guildId && x.UserId == userId).ToListAsync(cancellationToken);
        foreach (var standing in standings)
        {
            await PublishHistoricalAsync(settings, standing, true, cancellationToken);
            await PublishHistoricalAsync(settings, standing, false, cancellationToken);
        }
    }

    public async Task PublishGuildAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var settings = await database.GuildLeaderboardSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (settings == null) return;
        await hub.Clients.Group(HubGroupNames.LeaderboardAudience("public", settings.Alias)).SendAsync("leaderboardChanged", new LeaderboardSettingsChanged(settings.Alias, settings.Visibility), cancellationToken);
        await hub.Clients.Group(HubGroupNames.LeaderboardAudience("members", settings.Alias)).SendAsync("leaderboardChanged", new LeaderboardSettingsChanged(settings.Alias, settings.Visibility), cancellationToken);
    }

    public async Task PublishSettingsAsync(GuildLeaderboardSettings settings, string? previousAlias = null, CancellationToken cancellationToken = default)
    {
        foreach (var alias in new[] { previousAlias, settings.Alias }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
        {
            foreach (var audience in new[] { "public", "members" })
                await hub.Clients.Group(HubGroupNames.LeaderboardAudience(audience, alias!)).SendAsync("leaderboardChanged", new LeaderboardSettingsChanged(settings.Alias, settings.Visibility), cancellationToken);
        }
    }

    private async Task PublishLifetimeAsync(GuildLeaderboardSettings settings, MemberXp? member, bool membersAudience, CancellationToken cancellationToken)
    {
        if (!membersAudience && settings.Visibility != LeaderboardVisibility.Public) return;
        var group = HubGroupNames.Leaderboard(membersAudience ? "members" : "public", settings.Alias, SeasonLeaderboardScope.Lifetime, null);
        if (member == null || !member.IsCurrentMember || (!membersAudience && !member.PublicLeaderboardVisible))
        {
            if (member != null) await hub.Clients.Group(group).SendAsync("leaderboardEntryChanged", new LeaderboardEntryChanged(settings.Alias, SeasonLeaderboardScope.Lifetime, null, "remove", member.UserId.ToString(), null), cancellationToken);
            return;
        }
        var filter = Builders<MemberXp>.Filter.Eq(x => x.GuildId, settings.GuildId) & Builders<MemberXp>.Filter.Eq(x => x.IsCurrentMember, true);
        if (!membersAudience) filter &= Builders<MemberXp>.Filter.Eq(x => x.PublicLeaderboardVisible, true);
        var rank = 1 + await database.MemberXp.CountDocumentsAsync(filter & AheadOf(member.TotalXp, member.UserId), cancellationToken: cancellationToken);
        await hub.Clients.Group(group).SendAsync("leaderboardEntryChanged", new LeaderboardEntryChanged(settings.Alias, SeasonLeaderboardScope.Lifetime, null, "upsert", member.UserId.ToString(), ToDto(member, rank)), cancellationToken);
    }

    private async Task PublishCurrentSeasonAsync(GuildLeaderboardSettings settings, GuildSeason season, SeasonMemberXp? member, bool membersAudience, CancellationToken cancellationToken)
    {
        if (!membersAudience && settings.Visibility != LeaderboardVisibility.Public) return;
        var group = HubGroupNames.Leaderboard(membersAudience ? "members" : "public", settings.Alias, SeasonLeaderboardScope.CurrentSeason, null);
        if (member == null || !member.IsCurrentMember || (!membersAudience && !member.PublicLeaderboardVisible))
        {
            if (member != null) await hub.Clients.Group(group).SendAsync("leaderboardEntryChanged", new LeaderboardEntryChanged(settings.Alias, SeasonLeaderboardScope.CurrentSeason, null, "remove", member.UserId.ToString(), null), cancellationToken);
            return;
        }
        var filter = Builders<SeasonMemberXp>.Filter.Eq(x => x.SeasonId, season.Id) & Builders<SeasonMemberXp>.Filter.Eq(x => x.IsCurrentMember, true);
        if (!membersAudience) filter &= Builders<SeasonMemberXp>.Filter.Eq(x => x.PublicLeaderboardVisible, true);
        var rank = 1 + await database.SeasonMemberXp.CountDocumentsAsync(filter & SeasonAheadOf(member.TotalXp, member.UserId), cancellationToken: cancellationToken);
        await hub.Clients.Group(group).SendAsync("leaderboardEntryChanged", new LeaderboardEntryChanged(settings.Alias, SeasonLeaderboardScope.CurrentSeason, null, "upsert", member.UserId.ToString(), ToDto(member, rank)), cancellationToken);
    }

    private async Task PublishHistoricalAsync(GuildLeaderboardSettings settings, SeasonFinalStanding standing, bool membersAudience, CancellationToken cancellationToken)
    {
        if (!membersAudience && settings.Visibility != LeaderboardVisibility.Public) return;
        var group = HubGroupNames.Leaderboard(membersAudience ? "members" : "public", settings.Alias, SeasonLeaderboardScope.Season, standing.SeasonId);
        if (!membersAudience && !standing.PublicLeaderboardVisible)
        {
            await hub.Clients.Group(group).SendAsync("leaderboardEntryChanged", new LeaderboardEntryChanged(settings.Alias, SeasonLeaderboardScope.Season, standing.SeasonId, "remove", standing.UserId.ToString(), null), cancellationToken);
            return;
        }
        await hub.Clients.Group(group).SendAsync("leaderboardEntryChanged", new LeaderboardEntryChanged(settings.Alias, SeasonLeaderboardScope.Season, standing.SeasonId, "upsert", standing.UserId.ToString(), new(standing.Rank, standing.UserId.ToString(), standing.DisplayName, decimal.Truncate(standing.TotalXp), standing.Level, standing.MessageCount, standing.VoiceSeconds, false)), cancellationToken);
    }

    private static LeaderboardEntryDto ToDto(MemberXp member, long rank) => new(rank, member.UserId.ToString(), member.DisplayName, decimal.Truncate(member.TotalXp), Mee6LevelCurve.GetLevel(member.TotalXp), member.MessageCount, member.VoiceSeconds, false);
    private static LeaderboardEntryDto ToDto(SeasonMemberXp member, long rank) => new(rank, member.UserId.ToString(), member.DisplayName, decimal.Truncate(member.TotalXp), Mee6LevelCurve.GetLevel(member.TotalXp), member.MessageCount, member.VoiceSeconds, false);
    private static FilterDefinition<MemberXp> AheadOf(decimal xp, ulong userId) => Builders<MemberXp>.Filter.Or(Builders<MemberXp>.Filter.Gt(x => x.TotalXp, xp), Builders<MemberXp>.Filter.And(Builders<MemberXp>.Filter.Eq(x => x.TotalXp, xp), Builders<MemberXp>.Filter.Lt(x => x.UserId, userId)));
    private static FilterDefinition<SeasonMemberXp> SeasonAheadOf(decimal xp, ulong userId) => Builders<SeasonMemberXp>.Filter.Or(Builders<SeasonMemberXp>.Filter.Gt(x => x.TotalXp, xp), Builders<SeasonMemberXp>.Filter.And(Builders<SeasonMemberXp>.Filter.Eq(x => x.TotalXp, xp), Builders<SeasonMemberXp>.Filter.Lt(x => x.UserId, userId)));
}
