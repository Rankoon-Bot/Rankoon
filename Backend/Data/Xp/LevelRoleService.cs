using Discord.WebSocket;
using Rankoon.Data.Model;

namespace Rankoon.Data.Xp;

public sealed class LevelRoleService(DiscordShardedClient client, IXpService xp)
{
    public async Task SynchronizeAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default)
    {
        var guild = client.GetGuild(guildId); var member = guild?.GetUser(userId); if (guild == null || member == null) return;
        var settings = await xp.GetSettingsAsync(guildId, cancellationToken); var stats = await xp.GetMemberAsync(guildId, userId, cancellationToken); if (stats == null) return;
        var level = Mee6LevelCurve.GetLevel(stats.ImportedMee6Xp + stats.EarnedXp + stats.ManualAdjustment);
        var configured = settings.LevelRoles.Select(x => x.RoleId).ToHashSet();
        var deserved = settings.LevelRoles.Where(x => x.Level <= level).Select(x => x.RoleId).ToHashSet();
        foreach (var role in member.Roles.Where(x => configured.Contains(x.Id) && !deserved.Contains(x.Id))) await member.RemoveRoleAsync(role);
        foreach (var roleId in deserved.Where(roleId => !member.Roles.Any(x => x.Id == roleId))) { var role = guild.GetRole(roleId); if (role != null) await member.AddRoleAsync(role); }
    }
}
