using Discord;
using Discord.WebSocket;
using MongoDB.Bson;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using System.Collections.Concurrent;

namespace Rankoon.Data.Discord;

public sealed class SelfRoleValidationException(string errorKey) : Exception(errorKey)
{
    public string ErrorKey { get; } = errorKey;
}

public sealed class SelfRoleService(RankoonDbContext database, ILogger<SelfRoleService> logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> updateLocks = new();
    public async Task<SelfRolePanel> CreateAsync(SocketGuild guild, SelfRolePanel panel, CancellationToken cancellationToken = default)
    {
        panel.Id = ObjectId.GenerateNewId().ToString();
        panel.GuildId = guild.Id;
        panel.Revision = 1;
        Prepare(panel);
        await ValidateAsync(guild, panel);
        await PublishAsync(guild, panel, isNew: true);
        await database.SelfRolePanels.InsertOneAsync(panel, cancellationToken: cancellationToken);
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
            if (existing.Revision != panel.Revision) throw new SelfRoleValidationException("selfRoles.revisionConflict");

            panel.Id = existing.Id;
            panel.GuildId = guild.Id;
            panel.MessageId = existing.MessageId;
            panel.Revision++;
            Prepare(panel);
            await ValidateAsync(guild, panel);
            if (panel.ChannelId != existing.ChannelId)
            {
                await PublishAsync(guild, panel, isNew: true);
                await DeleteMessageAsync(guild, existing);
            }
            else await PublishAsync(guild, panel, isNew: false);
            var result = await database.SelfRolePanels.ReplaceOneAsync(x => x.Id == panelId && x.GuildId == guild.Id && x.Revision == existing.Revision, panel, cancellationToken: cancellationToken);
            if (result.MatchedCount == 0) throw new SelfRoleValidationException("selfRoles.revisionConflict");
            return panel;
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

    public async Task ReconcileAsync(IEnumerable<SocketGuild> guilds, CancellationToken cancellationToken = default)
    {
        foreach (var guild in guilds)
        foreach (var panel in await database.SelfRolePanels.Find(x => x.GuildId == guild.Id && x.Enabled).ToListAsync(cancellationToken))
        {
            try
            {
                if (guild.GetChannel(panel.ChannelId) is not IMessageChannel channel || await channel.GetMessageAsync(panel.MessageId) is not IUserMessage message) continue;
                foreach (var mapping in panel.Mappings) await message.AddReactionAsync(ToEmote(mapping.Emoji));
            }
            catch (Exception exception) { logger.LogWarning(exception, "Could not reconcile self-role panel {PanelId}", panel.Id); }
        }
    }

    public static bool Matches(SelfRoleEmoji emoji, IEmote reaction) => emoji.Kind == SelfRoleEmojiKind.Custom
        ? reaction is Emote custom && ulong.TryParse(emoji.Value, out var id) && custom.Id == id
        : reaction is Emoji unicode && unicode.Name == emoji.Value;

    private static void Prepare(SelfRolePanel panel)
    {
        panel.Title ??= string.Empty;
        panel.Description ??= string.Empty;
        panel.Color ??= string.Empty;
        panel.Mappings ??= [];
        panel.UpdatedAt = DateTime.UtcNow;
        panel.Status = panel.Enabled ? "Published" : "Disabled";
        foreach (var mapping in panel.Mappings) mapping.Id ??= ObjectId.GenerateNewId().ToString();
    }

    private async Task ValidateAsync(SocketGuild guild, SelfRolePanel panel)
    {
        if (panel.ChannelId == 0 || string.IsNullOrWhiteSpace(panel.Title) || panel.Title.Length > 256 || panel.Mappings is null || panel.Mappings.Count == 0) throw new SelfRoleValidationException("selfRoles.invalidPanel");
        if (panel.Mappings.Count > 20) throw new SelfRoleValidationException("selfRoles.tooManyMappings");
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
        if (!TryColor(panel.Color, out _)) throw new SelfRoleValidationException("selfRoles.invalidPanel");
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
                _ = ToEmote(mapping.Emoji);
                if (mapping.Emoji.Kind == SelfRoleEmojiKind.Custom && (!ulong.TryParse(mapping.Emoji.Value, out var emojiId) || !availableEmojiIds.Contains(emojiId)))
                    throw new ArgumentException("Custom emoji is not available on this guild.");
            }
            catch { throw new SelfRoleValidationException("selfRoles.emojiInvalid"); }
            var key = $"{mapping.Emoji.Kind}:{mapping.Emoji.Value}";
            if (!emojiKeys.Add(key)) throw new SelfRoleValidationException("selfRoles.duplicateEmoji");
        }
        var mappingLegendLength = panel.Mappings.Sum(mapping => (ToEmote(mapping.Emoji).ToString()?.Length ?? 0) + 23);
        if (panel.Description.Length + mappingLegendLength + (string.IsNullOrWhiteSpace(panel.Description) ? 0 : 2) > 4096) throw new SelfRoleValidationException("selfRoles.invalidPanel");
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
                message = await channel.SendMessageAsync(embed: BuildEmbed(panel));
                panel.MessageId = message.Id;
            }
            else
            {
                logger.LogInformation("Self-role Discord publish: updating message {MessageId} in guild {GuildId}, channel {ChannelId}, panel {PanelId}, mappings {MappingCount}", panel.MessageId, guild.Id, channel.Id, panel.Id, panel.Mappings.Count);
                if (await channel.GetMessageAsync(panel.MessageId) is not IUserMessage existing) throw new SelfRoleValidationException("selfRoles.channelNotUsable");
                message = existing;
                await message.ModifyAsync(properties => properties.Embed = BuildEmbed(panel));
                logger.LogDebug("Self-role Discord publish: removing existing reactions from message {MessageId}", message.Id);
                await message.RemoveAllReactionsAsync();
            }
            if (panel.Enabled)
            {
                foreach (var mapping in panel.Mappings)
                {
                    logger.LogDebug("Self-role Discord publish: adding reaction {Emoji} for role {RoleId} to message {MessageId}", mapping.Emoji.Value, mapping.RoleId, message.Id);
                    await message.AddReactionAsync(ToEmote(mapping.Emoji));
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

    private static Embed BuildEmbed(SelfRolePanel panel)
    {
        TryColor(panel.Color, out var color);
        var mappings = string.Join('\n', panel.Mappings.Select(mapping => $"{ToEmote(mapping.Emoji)} <@&{mapping.RoleId}>"));
        var description = string.IsNullOrWhiteSpace(panel.Description) ? mappings : $"{panel.Description}\n\n{mappings}";
        return new EmbedBuilder().WithTitle(panel.Title).WithDescription(description).WithColor(color).Build();
    }

    private static bool TryColor(string value, out Color color)
    {
        color = Color.Default;
        var hex = value.Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return false;
        color = new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private static IEmote ToEmote(SelfRoleEmoji emoji)
    {
        if (emoji.Kind == SelfRoleEmojiKind.Unicode && !string.IsNullOrWhiteSpace(emoji.Value)) return new Emoji(emoji.Value);
        if (emoji.Kind == SelfRoleEmojiKind.Custom && ulong.TryParse(emoji.Value, out var id) && !string.IsNullOrWhiteSpace(emoji.Name)) return new Emote(id, emoji.Name, false);
        throw new ArgumentException("Invalid emoji.");
    }

    private static async Task DeleteMessageAsync(SocketGuild guild, SelfRolePanel panel)
    {
        if (guild.GetChannel(panel.ChannelId) is not IMessageChannel channel) return;
        try { if (await channel.GetMessageAsync(panel.MessageId) is IUserMessage message) await message.DeleteAsync(); }
        catch (global::Discord.Net.HttpException exception) when (exception.HttpCode == System.Net.HttpStatusCode.NotFound) { }
    }
}
