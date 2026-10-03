using Discord;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;

namespace Rankoon.Data.Xp;

public sealed record LevelRoleChange(ulong RoleId, string Name, int RequiredLevel);
public sealed record LevelRoleFailure(ulong RoleId, string Name, int RequiredLevel, string ErrorCode);
public sealed record LevelRoleSynchronizationResult(IReadOnlyList<LevelRoleChange> Added, IReadOnlyList<LevelRoleChange> Removed, IReadOnlyList<LevelRoleFailure> Failed, IReadOnlyList<LevelRoleChange> AlreadyPresent);

public sealed class LevelRoleService(IGuildDiscordContextResolver discord, IXpService xp)
{
    public async Task<LevelRoleSynchronizationResult> SynchronizeAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default)
    {
        var added = new List<LevelRoleChange>(); var removed = new List<LevelRoleChange>(); var failed = new List<LevelRoleFailure>(); var alreadyPresent = new List<LevelRoleChange>();
        await using var guard = await LevelRoleSynchronizationLock.AcquireAsync(guildId, userId, cancellationToken);
        var context = await discord.ResolveAsync(guildId, cancellationToken); var guild = context?.Guild;
        if (guild == null) throw new InvalidOperationException("Discord guild is unavailable; retry role synchronization when the bot reconnects.");
        // Always read Discord's current state. A socket cache miss does not mean the member left.
        var member = await LevelRoleDiscordMember.FetchAsync(context!, userId, cancellationToken);
        if (member == null) return new(added, removed, failed, alreadyPresent);
        var settings = await xp.GetSettingsAsync(guildId, cancellationToken); var stats = await xp.GetMemberAsync(guildId, userId, cancellationToken); if (stats == null) return new(added, removed, failed, alreadyPresent);
        var level = Mee6LevelCurve.GetLevel(stats.ImportedMee6Xp + stats.EarnedXp + stats.ManualAdjustment);
        return await SynchronizeMemberAsync(member, settings.LevelRoles, level, id => guild.GetRole(id), cancellationToken);
    }

    internal static async Task<LevelRoleSynchronizationResult> SynchronizeMemberAsync(IGuildUser member, IEnumerable<LevelRole> definitions, int level, Func<ulong, IRole?> resolveRole, CancellationToken cancellationToken)
    {
        var added = new List<LevelRoleChange>(); var removed = new List<LevelRoleChange>(); var failed = new List<LevelRoleFailure>(); var alreadyPresent = new List<LevelRoleChange>();
        // Legacy duplicate definitions converge on the strictest threshold.
        var configured = definitions.GroupBy(x => x.RoleId).ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.Level).First());
        var deserved = configured.Values.Where(x => x.Level <= level).ToDictionary(x => x.RoleId);
        foreach (var roleId in member.RoleIds.Where(id => configured.ContainsKey(id) && !deserved.ContainsKey(id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var role = resolveRole(roleId);
            var change = new LevelRoleChange(roleId, role?.Name ?? roleId.ToString(), configured[roleId].Level);
            try { await member.RemoveRoleAsync(roleId, new RequestOptions { CancelToken = cancellationToken }); removed.Add(change); }
            catch (Exception exception) when (exception is not OperationCanceledException) { failed.Add(new(change.RoleId, change.Name, change.RequiredLevel, "removeFailed")); }
        }
        foreach (var definition in deserved.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var role = resolveRole(definition.RoleId); var change = new LevelRoleChange(definition.RoleId, role?.Name ?? definition.RoleId.ToString(), definition.Level);
            if (member.RoleIds.Contains(definition.RoleId)) { alreadyPresent.Add(change); continue; }
            if (role == null) { failed.Add(new(change.RoleId, change.Name, change.RequiredLevel, "roleNotFound")); continue; }
            try { await member.AddRoleAsync(role.Id, new RequestOptions { CancelToken = cancellationToken }); added.Add(change); }
            catch (Exception exception) when (exception is not OperationCanceledException) { failed.Add(new(change.RoleId, change.Name, change.RequiredLevel, "addFailed")); }
        }
        return new(added, removed, failed, alreadyPresent);
    }
}
