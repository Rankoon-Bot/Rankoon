using MongoDB.Driver;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Xp;

/// <summary>Synchronizes only roles owned by a concrete season and records bot ownership before later cleanup.</summary>
public sealed class SeasonLevelRoleService(RankoonDbContext database, IGuildDiscordContextResolver discord, TimeProvider timeProvider)
{
    public async Task<LevelRoleSynchronizationResult> SynchronizeAsync(ulong guildId, string seasonId, ulong userId, CancellationToken cancellationToken = default)
    {
        var added = new List<LevelRoleChange>(); var removed = new List<LevelRoleChange>(); var failed = new List<LevelRoleFailure>(); var present = new List<LevelRoleChange>();
        var season = await database.GuildSeasons.Find(x => x.Id == seasonId && x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        var stats = await database.SeasonMemberXp.Find(x => x.SeasonId == seasonId && x.UserId == userId).FirstOrDefaultAsync(cancellationToken);
        var guild = (await discord.ResolveAsync(guildId, cancellationToken))?.Guild; var member = guild?.GetUser(userId);
        if (season == null || stats == null || guild == null || member == null) return new(added, removed, failed, present);
        var level = Mee6LevelCurve.GetLevel(stats.TotalXp); var rules = season.SettingsSnapshot.SeasonLevelRoles.GroupBy(x => x.RoleId).Select(x => x.OrderByDescending(y => y.Level).First()).ToArray();
        var activeAssignments = await database.SeasonRoleAssignments.Find(x => x.SeasonId == seasonId && x.UserId == userId && x.RemovedAtUtc == null).ToListAsync(cancellationToken);
        foreach (var assignment in activeAssignments.Where(x => rules.All(rule => rule.RoleId != x.RoleId || rule.Level > level)))
        {
            var role = guild.GetRole(assignment.RoleId); var change = new LevelRoleChange(assignment.RoleId, role?.Name ?? assignment.RoleId.ToString(), assignment.RequiredLevel);
            if (role == null) { failed.Add(new(change.RoleId, change.Name, change.RequiredLevel, "roleNotFound")); continue; }
            try { await member.RemoveRoleAsync(role); await database.SeasonRoleAssignments.UpdateOneAsync(x => x.Id == assignment.Id && x.RemovedAtUtc == null, Builders<SeasonRoleAssignment>.Update.Set(x => x.RemovedAtUtc, timeProvider.GetUtcNow().UtcDateTime), cancellationToken: cancellationToken); removed.Add(change); }
            catch { failed.Add(new(change.RoleId, change.Name, change.RequiredLevel, "removeFailed")); }
        }
        foreach (var rule in rules.Where(x => x.Level <= level))
        {
            var role = guild.GetRole(rule.RoleId); var change = new LevelRoleChange(rule.RoleId, role?.Name ?? rule.RoleId.ToString(), rule.Level);
            if (role == null) { failed.Add(new(change.RoleId, change.Name, change.RequiredLevel, "roleNotFound")); continue; }
            if (member.Roles.Any(x => x.Id == rule.RoleId)) { present.Add(change); continue; }
            try
            {
                await member.AddRoleAsync(role);
                await database.SeasonRoleAssignments.UpdateOneAsync(x => x.SeasonId == seasonId && x.UserId == userId && x.RoleId == rule.RoleId,
                    Builders<SeasonRoleAssignment>.Update.SetOnInsert(x => x.GuildId, guildId).SetOnInsert(x => x.SeasonId, seasonId).SetOnInsert(x => x.UserId, userId).SetOnInsert(x => x.RoleId, rule.RoleId).SetOnInsert(x => x.RequiredLevel, rule.Level).SetOnInsert(x => x.Retention, rule.Retention).SetOnInsert(x => x.GrantedAtUtc, timeProvider.GetUtcNow().UtcDateTime).Set(x => x.RemovedAtUtc, null), new UpdateOptions { IsUpsert = true }, cancellationToken);
                added.Add(change);
            }
            catch { failed.Add(new(change.RoleId, change.Name, change.RequiredLevel, "addFailed")); }
        }
        return new(added, removed, failed, present);
    }

    public async Task FinalizeAsync(GuildSeason season, CancellationToken cancellationToken = default)
    {
        var assignments = await database.SeasonRoleAssignments.Find(x => x.SeasonId == season.Id && x.RemovedAtUtc == null && x.Retention == SeasonLevelRoleRetention.RemoveAtSeasonEnd).ToListAsync(cancellationToken);
        foreach (var user in assignments.GroupBy(x => x.UserId)) await SynchronizeEndAsync(season, user.Key, user.ToArray(), cancellationToken);
    }
    private async Task SynchronizeEndAsync(GuildSeason season, ulong userId, IReadOnlyList<SeasonRoleAssignment> assignments, CancellationToken cancellationToken)
    {
        var guild = (await discord.ResolveAsync(season.GuildId, cancellationToken))?.Guild; var member = guild?.GetUser(userId); if (guild == null || member == null) return;
        foreach (var assignment in assignments)
        {
            var role = guild.GetRole(assignment.RoleId); if (role == null) continue;
            try { await member.RemoveRoleAsync(role); await database.SeasonRoleAssignments.UpdateOneAsync(x => x.Id == assignment.Id && x.RemovedAtUtc == null, Builders<SeasonRoleAssignment>.Update.Set(x => x.RemovedAtUtc, timeProvider.GetUtcNow().UtcDateTime), cancellationToken: cancellationToken); } catch { }
        }
    }
}
