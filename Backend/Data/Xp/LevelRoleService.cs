using Discord.WebSocket;
using Rankoon.Data.Model;

namespace Rankoon.Data.Xp;

public sealed record LevelRoleChange(ulong RoleId, string Name, int RequiredLevel);
public sealed record LevelRoleFailure(ulong RoleId, string Name, int RequiredLevel, string ErrorCode);
public sealed record LevelRoleSynchronizationResult(IReadOnlyList<LevelRoleChange> Added, IReadOnlyList<LevelRoleChange> Removed, IReadOnlyList<LevelRoleFailure> Failed);

public sealed class LevelRoleService(DiscordShardedClient client, IXpService xp)
{
    public async Task<LevelRoleSynchronizationResult> SynchronizeAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default)
    {
        var added = new List<LevelRoleChange>(); var removed = new List<LevelRoleChange>(); var failed = new List<LevelRoleFailure>();
        var guild = client.GetGuild(guildId); var member = guild?.GetUser(userId); if (guild == null || member == null) return new(added, removed, failed);
        var settings = await xp.GetSettingsAsync(guildId, cancellationToken); var stats = await xp.GetMemberAsync(guildId, userId, cancellationToken); if (stats == null) return new(added, removed, failed);
        var level = Mee6LevelCurve.GetLevel(stats.ImportedMee6Xp + stats.EarnedXp + stats.ManualAdjustment);
        var configured = settings.LevelRoles.ToDictionary(x => x.RoleId);
        var deserved = configured.Values.Where(x => x.Level <= level).ToDictionary(x => x.RoleId);
        foreach (var role in member.Roles.Where(x => configured.ContainsKey(x.Id) && !deserved.ContainsKey(x.Id)))
        {
            var change = new LevelRoleChange(role.Id, role.Name, configured[role.Id].Level);
            try { await member.RemoveRoleAsync(role); removed.Add(change); } catch { failed.Add(new(change.RoleId, change.Name, change.RequiredLevel, "removeFailed")); }
        }
        foreach (var definition in deserved.Values.Where(x => !member.Roles.Any(role => role.Id == x.RoleId)))
        {
            var role = guild.GetRole(definition.RoleId); var change = new LevelRoleChange(definition.RoleId, role?.Name ?? definition.RoleId.ToString(), definition.Level);
            if (role == null) { failed.Add(new(change.RoleId, change.Name, change.RequiredLevel, "roleNotFound")); continue; }
            try { await member.AddRoleAsync(role); added.Add(change); } catch { failed.Add(new(change.RoleId, change.Name, change.RequiredLevel, "addFailed")); }
        }
        return new(added, removed, failed);
    }
}
