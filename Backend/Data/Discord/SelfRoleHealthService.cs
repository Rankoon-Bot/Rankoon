using Discord;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using System.Collections.Concurrent;

namespace Rankoon.Data.Discord;

public sealed class SelfRoleHealthService(RankoonDbContext database, ISelfRoleHealthChecker checker, IGuildDiscordContextResolver discord, TimeProvider timeProvider)
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> guildLocks = new();

    public async Task RefreshAsync(ulong guildId, CancellationToken cancellationToken = default, bool force = false, bool restoreMissingReactions = false)
    {
        var gate = guildLocks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var panels = await database.SelfRolePanels.Find(panel => panel.GuildId == guildId && panel.Enabled).ToListAsync(cancellationToken);
            var context = await discord.ResolveAsync(guildId, cancellationToken);
            IDiscordClient? client = context?.Guild.IsConnected == true ? context.Client.Rest : null;
            foreach (var panel in panels)
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;
                if (!force && panel.LastHealthCheckAt > now - CheckInterval) continue;
                var previousCheck = panel.LastHealthCheckAt;
                var result = await checker.CheckAsync(client, panel, restoreMissingReactions, cancellationToken);
                // Health observations must never overwrite a newer edit, repair, disable, or check.
                await database.SelfRolePanels.UpdateOneAsync(
                    current => current.Id == panel.Id && current.GuildId == guildId && current.Enabled &&
                        current.Revision == panel.Revision && current.MessageId == panel.MessageId && current.LastHealthCheckAt == previousCheck,
                    Builders<SelfRolePanel>.Update.Set(current => current.State, result.State).Set(current => current.Status, result.State.ToString())
                        .Set(current => current.LastHealthCheckAt, now).Set(current => current.LastError, result.ErrorKey)
                        .Set(current => current.LastErrorAt, result.ErrorKey == null ? (DateTime?)null : now),
                    cancellationToken: cancellationToken);
            }
        }
        finally { gate.Release(); }
    }
}
