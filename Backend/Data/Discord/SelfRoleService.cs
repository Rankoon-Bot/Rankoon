using Discord;
using Discord.WebSocket;
using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using System.Collections.Concurrent;

namespace Rankoon.Data.Discord;

public sealed class SelfRoleValidationException(string errorKey, IReadOnlyDictionary<string, object?>? parameters = null) : Exception(errorKey)
{
    public string ErrorKey { get; } = errorKey;
    public IReadOnlyDictionary<string, object?>? Parameters { get; } = parameters;
}

public sealed class SelfRoleService(RankoonDbContext database, TimeProvider timeProvider, ILogger<SelfRoleService> logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> updateLocks = new();
    public async Task<List<SelfRolePanel>> ListAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var panels = await database.SelfRolePanels.Find(x => x.GuildId == guildId).SortByDescending(x => x.UpdatedAt).ToListAsync(cancellationToken);
        foreach (var panel in panels) SelfRoleMessageRenderer.Normalize(panel);
        return panels;
    }
    public async Task<SelfRolePanel> CreateAsync(SocketGuild guild, SelfRolePanel panel, CancellationToken cancellationToken = default)
    {
        panel.Id = ObjectId.GenerateNewId().ToString();
        panel.GuildId = guild.Id;
        panel.Revision = 1;
        Prepare(panel);
        await ValidateAsync(guild, panel);
        try
        {
            await PublishAsync(guild, panel, isNew: true);
            MarkHealthy(panel);
            await database.SelfRolePanels.InsertOneAsync(panel, cancellationToken: cancellationToken);
        }
        catch
        {
            // A failed persistence must not leave a newly-created Discord message orphaned.
            if (panel.MessageId != 0) await DeleteMessageAsync(guild, panel);
            throw;
        }
        return panel;
    }

    public async Task<SelfRolePanel?> UpdateAsync(SocketGuild guild, string panelId, SelfRolePanel panel, CancellationToken cancellationToken = default)
    {
        var updateLock = updateLocks.GetOrAdd($"{guild.Id}:{panelId}", _ => new SemaphoreSlim(1, 1));
        await updateLock.WaitAsync(cancellationToken);
        try
        {
            var existing = await database.SelfRolePanels.Find(x => x.Id == panelId && x.GuildId == guild.Id).FirstOrDefaultAsync(cancellationToken);
            if (existing == null) return null;
            Prepare(existing);
            if (existing.Revision != panel.Revision) throw new SelfRoleValidationException("selfRoles.revisionConflict");

            panel.Id = existing.Id;
            panel.GuildId = guild.Id;
            panel.MessageId = existing.MessageId;
            panel.Revision++;
            Prepare(panel);
            await ValidateAsync(guild, panel);
            try
            {
                await PublishAsync(guild, panel, isNew: panel.ChannelId != existing.ChannelId);
                MarkHealthy(panel);
            }
            catch (Exception exception)
            {
                try
                {
                    if (panel.ChannelId != existing.ChannelId) await DeleteMessageAsync(guild, panel);
                    else await PublishAsync(guild, existing, isNew: false);
                }
                catch (Exception compensationException) { logger.LogWarning(compensationException, "Could not compensate failed self-role update for panel {PanelId}", panelId); }
                await MarkFailureAsync(existing, exception, cancellationToken);
                throw;
            }
            try
            {
                var result = await database.SelfRolePanels.ReplaceOneAsync(x => x.Id == panelId && x.GuildId == guild.Id && x.Revision == existing.Revision, panel, cancellationToken: cancellationToken);
                if (result.MatchedCount == 0) throw new SelfRoleValidationException("selfRoles.revisionConflict");
                if (panel.ChannelId != existing.ChannelId) await DeleteMessageAsync(guild, existing);
                return panel;
            }
            catch
            {
                // Restore the previous published configuration when its persistence did not succeed.
                if (panel.ChannelId != existing.ChannelId) await DeleteMessageAsync(guild, panel);
                else await PublishAsync(guild, existing, isNew: false);
                throw;
            }
        }
        finally { updateLock.Release(); }
    }

    public async Task<bool> DeleteAsync(SocketGuild guild, string panelId, CancellationToken cancellationToken = default)
    {
        var panel = await database.SelfRolePanels.Find(x => x.Id == panelId && x.GuildId == guild.Id).FirstOrDefaultAsync(cancellationToken);
        if (panel == null) return false;
        await DeleteMessageAsync(guild, panel);
        await database.SelfRolePanels.DeleteOneAsync(x => x.Id == panelId && x.GuildId == guild.Id, cancellationToken);
        // Deleting configuration never revokes roles; provenance is no longer needed once its panel is gone.
        await database.SelfRoleAssignments.DeleteManyAsync(x => x.GuildId == guild.Id && x.PanelId == panelId, cancellationToken);
        return true;
    }

    public async Task<SelfRolePanel?> RepairAsync(SocketGuild guild, string panelId, long revision, CancellationToken cancellationToken = default)
    {
        var updateLock = updateLocks.GetOrAdd($"{guild.Id}:{panelId}", _ => new SemaphoreSlim(1, 1));
        await updateLock.WaitAsync(cancellationToken);
        try
        {
            var panel = await database.SelfRolePanels.Find(x => x.Id == panelId && x.GuildId == guild.Id).FirstOrDefaultAsync(cancellationToken);
            if (panel == null) return null;
            Prepare(panel);
            if (panel.Revision != revision) throw new SelfRoleValidationException("selfRoles.revisionConflict");

            var recreated = false;
            try
            {
                await ValidateAsync(guild, panel);
                recreated = panel.MessageId == 0 || guild.GetChannel(panel.ChannelId) is not IMessageChannel channel || await channel.GetMessageAsync(panel.MessageId) is not IUserMessage;
                await PublishAsync(guild, panel, recreated);
                panel.Revision++;
                Prepare(panel);
                MarkHealthy(panel);
                var result = await database.SelfRolePanels.ReplaceOneAsync(x => x.Id == panelId && x.GuildId == guild.Id && x.Revision == revision, panel, cancellationToken: cancellationToken);
                if (result.MatchedCount == 0) throw new SelfRoleValidationException("selfRoles.revisionConflict");
                return panel;
            }
            catch (Exception exception)
            {
                if (recreated && panel.MessageId != 0) await DeleteMessageAsync(guild, panel);
                await MarkFailureAsync(panel, exception, cancellationToken);
                throw;
            }
        }
        finally { updateLock.Release(); }
    }

    public async Task ReconcileAsync(IEnumerable<SocketGuild> guilds, CancellationToken cancellationToken = default)
    {
        foreach (var guild in guilds)
        foreach (var panel in await database.SelfRolePanels.Find(x => x.GuildId == guild.Id && x.Enabled).ToListAsync(cancellationToken))
        {
            try
            {
                Prepare(panel);
                if (guild.GetChannel(panel.ChannelId) is not IMessageChannel channel || await channel.GetMessageAsync(panel.MessageId) is not IUserMessage message)
                {
                    await MarkFailureAsync(panel, new InvalidOperationException("The configured Discord message is unavailable."), cancellationToken);
                    continue;
                }
                foreach (var mapping in panel.Mappings) await message.AddReactionAsync(SelfRoleMessageRenderer.ToEmote(mapping.Emoji));
                await MarkHealthyAsync(panel, cancellationToken);
            }
            catch (Exception exception)
            {
                await MarkFailureAsync(panel, exception, cancellationToken);
                logger.LogWarning(exception, "Could not reconcile self-role panel {PanelId}", panel.Id);
            }
        }
    }

    public static bool Matches(SelfRoleEmoji emoji, IEmote reaction) => emoji.Kind == SelfRoleEmojiKind.Custom
        ? reaction is Emote custom && ulong.TryParse(emoji.Value, out var id) && custom.Id == id
        : reaction is Emoji unicode && unicode.Name == emoji.Value;

    private void Prepare(SelfRolePanel panel)
    {
        SelfRoleMessageRenderer.Normalize(panel);
        panel.Mappings ??= [];
        panel.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var mapping in panel.Mappings) mapping.Id ??= ObjectId.GenerateNewId().ToString();
    }

    private void MarkHealthy(SelfRolePanel panel)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        panel.State = panel.Enabled ? SelfRolePanelState.Published : SelfRolePanelState.Disabled;
        panel.Status = panel.State.ToString();
        panel.LastPublishedAt = now;
        panel.LastHealthCheckAt = now;
        panel.LastError = null;
        panel.LastErrorAt = null;
    }

    private async Task MarkHealthyAsync(SelfRolePanel panel, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await database.SelfRolePanels.UpdateOneAsync(x => x.Id == panel.Id && x.GuildId == panel.GuildId,
            Builders<SelfRolePanel>.Update.Set(x => x.State, panel.Enabled ? SelfRolePanelState.Published : SelfRolePanelState.Disabled).Set(x => x.Status, panel.Enabled ? "Published" : "Disabled").Set(x => x.LastHealthCheckAt, now).Set(x => x.LastError, null).Set(x => x.LastErrorAt, null), cancellationToken: cancellationToken);
    }

    private async Task MarkFailureAsync(SelfRolePanel panel, Exception exception, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var error = exception is SelfRoleValidationException validation ? validation.ErrorKey : "selfRoles.publishFailed";
        await database.SelfRolePanels.UpdateOneAsync(x => x.Id == panel.Id && x.GuildId == panel.GuildId,
            Builders<SelfRolePanel>.Update.Set(x => x.State, SelfRolePanelState.Degraded).Set(x => x.Status, "Degraded").Set(x => x.LastHealthCheckAt, now).Set(x => x.LastError, error).Set(x => x.LastErrorAt, now), cancellationToken: cancellationToken);
    }

    private async Task ValidateAsync(SocketGuild guild, SelfRolePanel panel)
    {
        if (panel.ChannelId == 0 || panel.Mappings is null || panel.Mappings.Count == 0) throw new SelfRoleValidationException("selfRoles.invalidPanel");
        if (panel.Mappings.Count > 20) throw new SelfRoleValidationException("selfRoles.tooManyMappings");
        SelfRoleMessageRenderer.ValidateStructure(panel);
        if (guild.GetChannel(panel.ChannelId) is not SocketTextChannel channel || channel.GetChannelType() is not (ChannelType.Text or ChannelType.News))
        {
            logger.LogWarning("Self-role validation rejected non-text or unavailable channel {ChannelId} in guild {GuildId}; bot {BotId}", panel.ChannelId, guild.Id, guild.CurrentUser.Id);
            throw new SelfRoleValidationException("selfRoles.channelNotUsable");
        }
        var permissions = guild.CurrentUser.GetPermissions(channel);
        var administrator = guild.CurrentUser.GuildPermissions.Administrator;
        logger.LogInformation("Self-role channel permission check: guild {GuildId}, channel {ChannelId} ({ChannelName}), bot {BotId}, administrator {Administrator}, hierarchy {Hierarchy}, view {ViewChannel}, send {SendMessages}, embed {EmbedLinks}, react {AddReactions}, history {ReadMessageHistory}, manageMessages {ManageMessages}",
            guild.Id, channel.Id, channel.Name, guild.CurrentUser.Id, administrator, guild.CurrentUser.Hierarchy, permissions.ViewChannel, permissions.SendMessages, permissions.EmbedLinks, permissions.AddReactions, permissions.ReadMessageHistory, permissions.ManageMessages);
        if (!administrator && (!permissions.ViewChannel || !permissions.SendMessages || !permissions.EmbedLinks || !permissions.AddReactions || !permissions.ReadMessageHistory || !permissions.ManageMessages))
        {
            logger.LogWarning("Self-role validation rejected channel {ChannelId} in guild {GuildId} because the calculated channel permissions are incomplete", channel.Id, guild.Id);
            throw new SelfRoleValidationException("selfRoles.discordPermissions");
        }
        var customEmojiIds = panel.Mappings.Where(mapping => mapping.Emoji.Kind == SelfRoleEmojiKind.Custom).Select(mapping => mapping.Emoji.Value).ToHashSet(StringComparer.Ordinal);
        var availableEmojiIds = new HashSet<ulong>();
        if (customEmojiIds.Count > 0)
        {
            try { availableEmojiIds = (await guild.GetEmotesAsync()).Select(emoji => emoji.Id).ToHashSet(); }
            catch (global::Discord.Net.HttpException exception) when (exception.HttpCode == System.Net.HttpStatusCode.Forbidden)
            {
                logger.LogWarning(exception, "Could not validate server emojis through Discord REST for guild {GuildId}", guild.Id);
                throw new SelfRoleValidationException("selfRoles.discordPermissions");
            }
        }
        var emojiKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in panel.Mappings)
        {
            if (!guild.CurrentUser.GuildPermissions.ManageRoles || mapping.RoleId == 0 || guild.GetRole(mapping.RoleId) is not { } role || role.IsManaged || role.IsEveryone || role.Position >= guild.CurrentUser.Hierarchy)
                throw new SelfRoleValidationException("selfRoles.roleNotManageable");
            try
            {
                _ = SelfRoleMessageRenderer.ToEmote(mapping.Emoji);
                if (mapping.Emoji.Kind == SelfRoleEmojiKind.Custom && (!ulong.TryParse(mapping.Emoji.Value, out var emojiId) || !availableEmojiIds.Contains(emojiId)))
                    throw new ArgumentException("Custom emoji is not available on this guild.");
            }
            catch { throw new SelfRoleValidationException("selfRoles.emojiInvalid"); }
            var key = $"{mapping.Emoji.Kind}:{mapping.Emoji.Value}";
            if (!emojiKeys.Add(key)) throw new SelfRoleValidationException("selfRoles.duplicateEmoji");
        }
    }

    private async Task PublishAsync(SocketGuild guild, SelfRolePanel panel, bool isNew)
    {
        try
        {
            var channel = guild.GetChannel(panel.ChannelId) as IMessageChannel ?? throw new SelfRoleValidationException("selfRoles.channelNotUsable");
            IUserMessage message;
            if (isNew)
            {
                logger.LogInformation("Self-role Discord publish: sending embed to guild {GuildId}, channel {ChannelId}, panel {PanelId}, mappings {MappingCount}", guild.Id, channel.Id, panel.Id, panel.Mappings.Count);
                message = await channel.SendMessageAsync(embeds: SelfRoleMessageRenderer.BuildEmbeds(panel));
                panel.MessageId = message.Id;
            }
            else
            {
                logger.LogInformation("Self-role Discord publish: updating message {MessageId} in guild {GuildId}, channel {ChannelId}, panel {PanelId}, mappings {MappingCount}", panel.MessageId, guild.Id, channel.Id, panel.Id, panel.Mappings.Count);
                if (await channel.GetMessageAsync(panel.MessageId) is not IUserMessage existing) throw new SelfRoleValidationException("selfRoles.channelNotUsable");
                message = existing;
                await message.ModifyAsync(properties => properties.Embeds = SelfRoleMessageRenderer.BuildEmbeds(panel));
                logger.LogDebug("Self-role Discord publish: removing existing reactions from message {MessageId}", message.Id);
                await message.RemoveAllReactionsAsync();
            }
            if (panel.Enabled)
            {
                foreach (var mapping in panel.Mappings)
                {
                    logger.LogDebug("Self-role Discord publish: adding reaction {Emoji} for role {RoleId} to message {MessageId}", mapping.Emoji.Value, mapping.RoleId, message.Id);
                    try { await message.AddReactionAsync(SelfRoleMessageRenderer.ToEmote(mapping.Emoji)); }
                    catch (global::Discord.Net.HttpException exception) when (exception.DiscordCode == (DiscordErrorCode)10014)
                    {
                        var emoji = SelfRoleMessageRenderer.ToEmote(mapping.Emoji).ToString();
                        logger.LogWarning(exception, "Discord rejected self-role emoji {Emoji} for role {RoleId} on message {MessageId} in panel {PanelId}", emoji, mapping.RoleId, message.Id, panel.Id);
                        throw new SelfRoleValidationException("selfRoles.emojiRejected", new Dictionary<string, object?> { ["emoji"] = emoji });
                    }
                }
            }
        }
        catch (global::Discord.Net.HttpException exception) when (exception.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            logger.LogWarning(exception, "Self-role Discord request was forbidden: guild {GuildId}, channel {ChannelId}, message {MessageId}, panel {PanelId}, HTTP {HttpStatus}, DiscordCode {DiscordCode}, reason {Reason}",
                guild.Id, panel.ChannelId, panel.MessageId, panel.Id, exception.HttpCode, exception.DiscordCode, exception.Message);
            throw new SelfRoleValidationException("selfRoles.discordPermissions");
        }
    }

    private static async Task DeleteMessageAsync(SocketGuild guild, SelfRolePanel panel)
    {
        if (guild.GetChannel(panel.ChannelId) is not IMessageChannel channel) return;
        try { if (await channel.GetMessageAsync(panel.MessageId) is IUserMessage message) await message.DeleteAsync(); }
        catch (global::Discord.Net.HttpException exception) when (exception.HttpCode == System.Net.HttpStatusCode.NotFound) { }
    }
}
