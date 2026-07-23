using Discord;
using Discord.WebSocket;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Discord;

public sealed class SelfRoleReactionService(RankoonDbContext database, SelfRoleService panels, TimeProvider timeProvider, ILogger<SelfRoleReactionService> logger)
{
    public async Task OnShardReadyAsync(DiscordSocketClient shard)
    {
        try { await panels.ReconcileAsync(shard.Guilds); }
        catch (Exception exception) { logger.LogError(exception, "Self-role reconciliation failed for shard {ShardId}", shard.ShardId); }
    }
    public async Task OnGuildReadyAsync(SocketGuild guild)
    {
        try { await panels.ReconcileAsync([guild]); }
        catch (Exception exception) { logger.LogError(exception, "Self-role reconciliation failed for guild {GuildId}", guild.Id); }
    }

    public Task OnReactionAddedAsync(DiscordShardedClient client, Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction) => HandleAsync(client, message.Id, channel, reaction, true);
    public Task OnReactionRemovedAsync(DiscordShardedClient client, Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction) => HandleAsync(client, message.Id, channel, reaction, false);

    private async Task HandleAsync(DiscordShardedClient client, ulong messageId, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction, bool added)
    {
        try
        {
            var reactionChannel = await channel.GetOrDownloadAsync();
            if (reactionChannel is not SocketGuildChannel guildChannel || (reaction.User.IsSpecified && reaction.User.Value.IsBot)) return;
            var panel = await database.SelfRolePanels.Find(x => x.GuildId == guildChannel.Guild.Id && x.MessageId == messageId && x.Enabled).FirstOrDefaultAsync();
            var mapping = panel?.Mappings.FirstOrDefault(item => SelfRoleService.Matches(item.Emoji, reaction.Emote));
            if (panel == null || mapping == null) return;
            IGuildUser? member = reaction.User.IsSpecified ? reaction.User.Value as IGuildUser : guildChannel.Guild.GetUser(reaction.UserId);
            member ??= await client.Rest.GetGuildUserAsync(guildChannel.Guild.Id, reaction.UserId);
            var role = guildChannel.Guild.GetRole(mapping.RoleId);
            if (member == null || member.IsBot || role == null) return;
            if (added)
            {
                if (role.Position >= guildChannel.Guild.CurrentUser.Hierarchy) return;
                var assignmentFilter = Builders<SelfRoleAssignment>.Filter.Where(x => x.GuildId == panel.GuildId && x.UserId == member.Id && x.RoleId == role.Id);
                // A role already held by the member is only claimed when another self-role
                // mapping already owns it. This preserves roles assigned outside Rankoon.
                if (member.RoleIds.Contains(role.Id) && !await database.SelfRoleAssignments.Find(assignmentFilter).AnyAsync()) return;
                if (!member.RoleIds.Contains(role.Id)) await member.AddRoleAsync(role);
                await database.SelfRoleAssignments.UpdateOneAsync(x => x.GuildId == panel.GuildId && x.PanelId == panel.Id && x.MappingId == mapping.Id && x.UserId == member.Id,
                    Builders<SelfRoleAssignment>.Update.SetOnInsert(x => x.GuildId, panel.GuildId).SetOnInsert(x => x.PanelId, panel.Id!).SetOnInsert(x => x.MappingId, mapping.Id!).SetOnInsert(x => x.UserId, member.Id).SetOnInsert(x => x.RoleId, role.Id).SetOnInsert(x => x.AssignedAt, timeProvider.GetUtcNow().UtcDateTime), new UpdateOptions { IsUpsert = true });
            }
            else
            {
                var assignment = await database.SelfRoleAssignments.FindOneAndDeleteAsync(x => x.GuildId == panel.GuildId && x.PanelId == panel.Id && x.MappingId == mapping.Id && x.UserId == member.Id);
                if (assignment != null && !await database.SelfRoleAssignments.Find(x => x.GuildId == panel.GuildId && x.UserId == member.Id && x.RoleId == role.Id).AnyAsync() && role.Position < guildChannel.Guild.CurrentUser.Hierarchy)
                    await member.RemoveRoleAsync(role);
            }
        }
        catch (Exception exception) { logger.LogError(exception, "Self-role reaction handling failed for message {MessageId}", messageId); }
    }
}
