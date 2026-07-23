using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Rankoon.Data.Xp;
using Rankoon.Data.Reporting;
using Rankoon.Data.MongoDb;
using MongoDB.Driver;

namespace Rankoon.Data.Discord;

/// <summary>Gateway adapters for non-voice XP sources. All awards use the idempotent ledger pipeline.</summary>
public sealed class ActivityXpEventService(IXpService xp, ServerBoosterXpMultiplierResolver boosterMultipliers, IReportWriter reports, RankoonDbContext database, TimeProvider timeProvider, ILogger<ActivityXpEventService> logger)
{
    public async Task OnMessageAsync(SocketMessage message)
    {
        try { await HandleMessageAsync(message); }
        catch (Exception exception) { logger.LogError(exception, "Message XP event failed for message {MessageId}", message.Id); if (message.Channel is SocketGuildChannel channel) await reports.WriteErrorAsync(channel.Guild.Id, "xp.message", exception, message.Author.Id, new Dictionary<string, object?> { ["channelId"] = channel.Id }); }
    }
    private async Task HandleMessageAsync(SocketMessage message)
    {
        if (message.Author.IsBot || message.Channel is not SocketGuildChannel channel) return;
        var settings = await xp.GetSettingsAsync(channel.Guild.Id);
        if (!settings.Enabled || settings.ExcludedChannelIds.Contains(channel.Id) || (channel is SocketTextChannel text && text.CategoryId.HasValue && settings.ExcludedCategoryIds.Contains(text.CategoryId.Value))) return;
        var member = message.Author as IGuildUser ?? channel.Guild.GetUser(message.Author.Id);
        if (member?.RoleIds.Any(settings.ExcludedRoleIds.Contains) == true) return;
        var source = channel is SocketThreadChannel ? "thread_message" : "message";
        var rule = source == "message" ? settings.Message : null;
        if (source == "message" && !rule!.Enabled) return;
        if (source == "thread_message" && !settings.Thread.Enabled) return;
        var baseAmount = source == "message" ? CalculateMessagePoints(message.Content.Length, rule!) : settings.Thread.MessagePoints;
        var award = boosterMultipliers.Apply(source, baseAmount, 1m, settings, member);
        var cooldown = source == "message" ? rule!.CooldownSeconds : settings.Thread.CooldownSeconds;
        var request = new XpGrantRequest(channel.Guild.Id, message.Author.Id, member?.DisplayName ?? message.Author.GlobalName ?? message.Author.Username, source, award.Amount, $"{source}:{message.Id}", timeProvider.GetUtcNow().UtcDateTime, channel.Id, CooldownSeconds: cooldown, AppliedServerBoosterMultiplier: award.AppliedServerBoosterMultiplier);
        await xp.GrantAsync(request);
    }

    public async Task OnReactionAsync(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
    {
        try { await HandleReactionAsync(message, channel, reaction); }
        catch (Exception exception) { logger.LogError(exception, "Reaction XP event failed for message {MessageId}", message.Id); if (reaction.User.IsSpecified && reaction.User.Value is SocketGuildUser member) await reports.WriteErrorAsync(member.Guild.Id, "xp.reaction", exception, reaction.UserId, new Dictionary<string, object?> { ["channelId"] = channel.Id }); }
    }
    private async Task HandleReactionAsync(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
    {
        var reactingUser = reaction.User.IsSpecified ? reaction.User.Value : null;
        var reactionChannel = await channel.GetOrDownloadAsync();
        if (reactingUser?.IsBot != false || reactionChannel is not SocketGuildChannel guildChannel) return;
        if (await database.SelfRolePanels.Find(panel => panel.GuildId == guildChannel.Guild.Id && panel.MessageId == message.Id && panel.Enabled).AnyAsync()) return;
        var settings = await xp.GetSettingsAsync(guildChannel.Guild.Id);
        if (!settings.Enabled || !settings.Reaction.Enabled || settings.ExcludedChannelIds.Contains(guildChannel.Id)) return;
        var member = reactingUser as IGuildUser ?? guildChannel.Guild.GetUser(reaction.UserId);
        if (member?.RoleIds.Any(settings.ExcludedRoleIds.Contains) == true) return;
        var award = boosterMultipliers.Apply("reaction", settings.Reaction.Points, 1m, settings, member);
        var request = new XpGrantRequest(guildChannel.Guild.Id, reaction.UserId, member?.DisplayName ?? reactingUser.Username, "reaction", award.Amount, $"reaction:{message.Id}:{reaction.UserId}:{reaction.Emote}", timeProvider.GetUtcNow().UtcDateTime, guildChannel.Id, CooldownSeconds: settings.Reaction.CooldownSeconds, AppliedServerBoosterMultiplier: award.AppliedServerBoosterMultiplier);
        await xp.GrantAsync(request);
    }

    public async Task OnReactionRemovedAsync(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
    {
        try { await HandleReactionRemovedAsync(message, channel, reaction); }
        catch (Exception exception) { logger.LogError(exception, "Reaction XP removal failed for message {MessageId}", message.Id); }
    }
    private async Task HandleReactionRemovedAsync(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
    {
        var reactionChannel = await channel.GetOrDownloadAsync();
        if (reactionChannel is not SocketGuildChannel guildChannel) return;
        var settings = await xp.GetSettingsAsync(guildChannel.Guild.Id);
        if (!settings.Reaction.ReverseOnRemove) return;
        var originalKey = $"reaction:{message.Id}:{reaction.UserId}:{reaction.Emote}";
        await xp.ReverseGrantAsync(originalKey, $"reaction-remove:{message.Id}:{reaction.UserId}:{reaction.Emote}", "reaction");
    }

    public async Task OnThreadAsync(SocketThreadChannel thread)
    {
        try { await HandleThreadAsync(thread); }
        catch (Exception exception) { logger.LogError(exception, "Thread XP event failed for thread {ThreadId}", thread.Id); await reports.WriteErrorAsync(thread.Guild.Id, "xp.thread", exception, thread.Owner?.Id, new Dictionary<string, object?> { ["channelId"] = thread.Id }); }
    }
    private async Task HandleThreadAsync(SocketThreadChannel thread)
    {
        var owner = thread.Owner; if (owner == null || owner.IsBot) return;
        var settings = await xp.GetSettingsAsync(thread.Guild.Id);
        if (!settings.Enabled || !settings.Thread.Enabled) return;
        var award = boosterMultipliers.Apply("thread_create", settings.Thread.CreatePoints, 1m, settings, owner);
        await xp.GrantAsync(new XpGrantRequest(thread.Guild.Id, owner.Id, owner.DisplayName, "thread_create", award.Amount, $"thread:{thread.Id}", timeProvider.GetUtcNow().UtcDateTime, thread.Id, AppliedServerBoosterMultiplier: award.AppliedServerBoosterMultiplier));
    }

    public async Task OnEventInterestAsync(Cacheable<SocketUser, RestUser, IUser, ulong> cachedUser, SocketGuildEvent guildEvent)
    {
        try { await HandleEventInterestAsync(cachedUser, guildEvent); }
        catch (Exception exception) { logger.LogError(exception, "Event interest XP event failed for event {EventId}", guildEvent.Id); await reports.WriteErrorAsync(guildEvent.Guild.Id, "xp.event_interest", exception, metadata: new Dictionary<string, object?> { ["eventId"] = guildEvent.Id }); }
    }
    private async Task HandleEventInterestAsync(Cacheable<SocketUser, RestUser, IUser, ulong> cachedUser, SocketGuildEvent guildEvent)
    {
        var user = await cachedUser.GetOrDownloadAsync();
        if (user == null) return;
        if (user.IsBot) return;
        var settings = await xp.GetSettingsAsync(guildEvent.Guild.Id);
        if (!settings.Enabled || !settings.EventInterest.Enabled) return;
        var member = user as IGuildUser ?? guildEvent.Guild.GetUser(user.Id);
        var award = boosterMultipliers.Apply("event_interest", settings.EventInterest.Points, 1m, settings, member);
        await xp.GrantAsync(new XpGrantRequest(guildEvent.Guild.Id, user.Id, member?.DisplayName ?? user.GlobalName ?? user.Username, "event_interest", award.Amount, $"event-interest:{guildEvent.Id}:{user.Id}", timeProvider.GetUtcNow().UtcDateTime, AppliedServerBoosterMultiplier: award.AppliedServerBoosterMultiplier));
    }
    public async Task OnEventInterestRemovedAsync(Cacheable<SocketUser, RestUser, IUser, ulong> cachedUser, SocketGuildEvent guildEvent)
    {
        try { await HandleEventInterestRemovedAsync(cachedUser, guildEvent); }
        catch (Exception exception) { logger.LogError(exception, "Event interest removal failed for event {EventId}", guildEvent.Id); await reports.WriteErrorAsync(guildEvent.Guild.Id, "xp.event_interest.remove", exception, metadata: new Dictionary<string, object?> { ["eventId"] = guildEvent.Id }); }
    }
    private async Task HandleEventInterestRemovedAsync(Cacheable<SocketUser, RestUser, IUser, ulong> cachedUser, SocketGuildEvent guildEvent)
    {
        var user = await cachedUser.GetOrDownloadAsync();
        if (user == null) return;
        await xp.ReverseGrantAsync($"event-interest:{guildEvent.Id}:{user.Id}", $"event-interest-remove:{guildEvent.Id}:{user.Id}", "event_interest");
    }
    private static decimal CalculateMessagePoints(int length, Data.Model.MessageXpSettings settings)
    {
        var capped = Math.Clamp(length, settings.MinimumCharacters, Math.Max(settings.MinimumCharacters, settings.MaximumCharacters));
        var range = Math.Max(1, settings.MaximumCharacters - settings.MinimumCharacters);
        return decimal.Floor(settings.MinimumPoints + (decimal)(capped - settings.MinimumCharacters) / range * (settings.MaximumPoints - settings.MinimumPoints));
    }
}
