using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Rankoon.Data.Xp;
using Rankoon.Data.Operations;
using Rankoon.Data.MongoDb;
using MongoDB.Driver;

namespace Rankoon.Data.Discord;

/// <summary>Gateway adapters for non-voice XP sources. All awards use the idempotent ledger pipeline.</summary>
public sealed class ActivityXpEventService(IXpService xp, ServerBoosterXpMultiplierResolver boosterMultipliers, IOperationalErrorRecorder errors, RankoonDbContext database, TimeProvider timeProvider, ILogger<ActivityXpEventService> logger)
{
    private readonly ActivityXpGrantProcessor grants = new(xp, boosterMultipliers, timeProvider);
    public async Task OnMessageAsync(SocketMessage message)
    {
        try { await XpIngressRetry.RunAsync(() => HandleMessageAsync(message), timeProvider); }
        catch (Exception exception) { logger.LogError(exception, "Message XP event failed for message {MessageId}", message.Id); if (message.Channel is SocketGuildChannel channel) await errors.RecordAsync(new(exception, "discord", "xp.message", GuildId: channel.Guild.Id, ActorUserId: message.Author.Id, ChannelId: channel.Id)); }
    }
    private async Task HandleMessageAsync(SocketMessage message)
    {
        if (message.Author.IsBot || message.Channel is not SocketGuildChannel channel) return;
        var member = message.Author as IGuildUser ?? channel.Guild.GetUser(message.Author.Id);
        member ??= await ((IGuild)channel.Guild).GetUserAsync(message.Author.Id);
        if (member == null) return;
        var parent = (channel as SocketThreadChannel)?.ParentChannel;
        var categoryId = (parent as INestedChannel)?.CategoryId ?? (channel as INestedChannel)?.CategoryId;
        var source = channel is SocketThreadChannel ? "thread_message" : "message";
        await grants.GrantAsync(new(channel.Guild.Id, message.Id, source, ActivityXpMember.From(member), channel.Id, categoryId, parent?.Id, message.Content.Length));
    }

    public async Task OnReactionAsync(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
    {
        try { await XpIngressRetry.RunAsync(() => HandleReactionAsync(message, channel, reaction), timeProvider); }
        catch (Exception exception) { logger.LogError(exception, "Reaction XP event failed for message {MessageId}", message.Id); if (reaction.User.IsSpecified && reaction.User.Value is SocketGuildUser member) await errors.RecordAsync(new(exception, "discord", "xp.reaction", GuildId: member.Guild.Id, ActorUserId: reaction.UserId, ChannelId: channel.Id)); }
    }
    private async Task HandleReactionAsync(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
    {
        var reactionChannel = await channel.GetOrDownloadAsync();
        if (reactionChannel is not IGuildChannel guildChannel) return;
        var member = reaction.User.IsSpecified ? reaction.User.Value as IGuildUser : null;
        member ??= await guildChannel.Guild.GetUserAsync(reaction.UserId);
        if (member == null || member.IsBot) return;
        if (await database.SelfRolePanels.Find(panel => panel.GuildId == guildChannel.GuildId && panel.MessageId == message.Id && panel.Enabled).AnyAsync()) return;
        var parent = (guildChannel as SocketThreadChannel)?.ParentChannel?.Id ?? (guildChannel as RestThreadChannel)?.ParentChannelId;
        var categoryId = (guildChannel as INestedChannel)?.CategoryId;
        if (parent.HasValue && await guildChannel.Guild.GetChannelAsync(parent.Value) is INestedChannel parentChannel) categoryId = parentChannel.CategoryId;
        await grants.GrantAsync(new(guildChannel.GuildId, message.Id, "reaction", ActivityXpMember.From(member), guildChannel.Id, categoryId, parent, Emote: reaction.Emote.ToString()));
    }

    public async Task OnReactionRemovedAsync(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
    {
        try { await XpIngressRetry.RunAsync(() => HandleReactionRemovedAsync(message, channel, reaction), timeProvider); }
        catch (Exception exception) { logger.LogError(exception, "Reaction XP removal failed for message {MessageId}", message.Id); if (reaction.User.IsSpecified && reaction.User.Value is SocketGuildUser member) await errors.RecordAsync(new(exception, "discord", "xp.reaction.remove", GuildId: member.Guild.Id, ActorUserId: reaction.UserId, ChannelId: channel.Id)); }
    }
    private async Task HandleReactionRemovedAsync(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
    {
        var reactionChannel = await channel.GetOrDownloadAsync();
        if (reactionChannel is not IGuildChannel guildChannel) return;
        var settings = await xp.GetSettingsAsync(guildChannel.GuildId);
        if (!settings.Reaction.ReverseOnRemove) return;
        var originalKey = $"reaction:{message.Id}:{reaction.UserId}:{reaction.Emote}";
        await xp.ReverseGrantAsync(originalKey, $"reaction-remove:{message.Id}:{reaction.UserId}:{reaction.Emote}", "reaction");
    }

    public async Task OnThreadAsync(SocketThreadChannel thread)
    {
        try { await XpIngressRetry.RunAsync(() => HandleThreadAsync(thread), timeProvider); }
        catch (Exception exception) { logger.LogError(exception, "Thread XP event failed for thread {ThreadId}", thread.Id); await errors.RecordAsync(new(exception, "discord", "xp.thread", GuildId: thread.Guild.Id, ActorUserId: thread.Owner?.Id, ChannelId: thread.Id)); }
    }
    private async Task HandleThreadAsync(SocketThreadChannel thread)
    {
        var ownerId = ((IThreadChannel)thread).OwnerId;
        IGuildUser? owner = thread.Guild.GetUser(ownerId);
        owner ??= await ((IGuild)thread.Guild).GetUserAsync(ownerId);
        if (owner == null || owner.IsBot) return;
        await grants.GrantAsync(new(thread.Guild.Id, thread.Id, "thread_create", ActivityXpMember.From(owner), thread.Id, (thread.ParentChannel as INestedChannel)?.CategoryId, thread.ParentChannel?.Id));
    }

    public async Task OnEventInterestAsync(Cacheable<SocketUser, RestUser, IUser, ulong> cachedUser, SocketGuildEvent guildEvent)
    {
        try { await XpIngressRetry.RunAsync(() => HandleEventInterestAsync(cachedUser, guildEvent), timeProvider); }
        catch (Exception exception) { logger.LogError(exception, "Event interest XP event failed for event {EventId}", guildEvent.Id); await errors.RecordAsync(new(exception, "discord", "xp.event_interest", GuildId: guildEvent.Guild.Id, Context: new Dictionary<string, object?> { ["eventId"] = guildEvent.Id })); }
    }
    private async Task HandleEventInterestAsync(Cacheable<SocketUser, RestUser, IUser, ulong> cachedUser, SocketGuildEvent guildEvent)
    {
        var user = await cachedUser.GetOrDownloadAsync();
        if (user == null) return;
        if (user.IsBot) return;
        var member = user as IGuildUser ?? guildEvent.Guild.GetUser(user.Id);
        member ??= await ((IGuild)guildEvent.Guild).GetUserAsync(user.Id);
        if (member == null) return;
        await grants.GrantAsync(new(guildEvent.Guild.Id, guildEvent.Id, "event_interest", ActivityXpMember.From(member), guildEvent.Channel?.Id, (guildEvent.Channel as INestedChannel)?.CategoryId));
    }
    public async Task OnEventInterestRemovedAsync(Cacheable<SocketUser, RestUser, IUser, ulong> cachedUser, SocketGuildEvent guildEvent)
    {
        try { await XpIngressRetry.RunAsync(() => HandleEventInterestRemovedAsync(cachedUser, guildEvent), timeProvider); }
        catch (Exception exception) { logger.LogError(exception, "Event interest removal failed for event {EventId}", guildEvent.Id); await errors.RecordAsync(new(exception, "discord", "xp.event_interest.remove", GuildId: guildEvent.Guild.Id, Context: new Dictionary<string, object?> { ["eventId"] = guildEvent.Id })); }
    }
    private async Task HandleEventInterestRemovedAsync(Cacheable<SocketUser, RestUser, IUser, ulong> cachedUser, SocketGuildEvent guildEvent)
    {
        var user = await cachedUser.GetOrDownloadAsync();
        if (user == null) return;
        await xp.ReverseGrantAsync($"event-interest:{guildEvent.Id}:{user.Id}", $"event-interest-remove:{guildEvent.Id}:{user.Id}", "event_interest");
    }
}
