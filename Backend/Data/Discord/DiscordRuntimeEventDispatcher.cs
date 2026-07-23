using System.Collections.Concurrent;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Rankoon.Data.Model;

namespace Rankoon.Data.Discord;

public interface IDiscordRuntimeEventDispatcher
{
    void Attach(string runtimeId, BotIdentityMode mode, DiscordShardedClient client, ulong? assignedGuildId = null);
    void Detach(string runtimeId);
}

public sealed class DiscordRuntimeEventDispatcher(
    DiscordShardedClient platform,
    IGuildBotAuthority authority,
    ActivityXpEventService activity,
    VoiceXpWatchdog voice,
    VcHubService hubs,
    SelfRoleReactionService selfRoles,
    GuildMembershipService memberships,
    ApplicationCommandRegistrar commands,
    RankoonInteractionHandler interactions,
    ILogger<DiscordRuntimeEventDispatcher> logger) : IDiscordRuntimeEventDispatcher, IHostedService
{
    private readonly ConcurrentDictionary<string, Binding> bindings = new(StringComparer.Ordinal);
    private readonly ActivityXpEventService activity = activity;
    private readonly VoiceXpWatchdog voice = voice;
    private readonly VcHubService hubs = hubs;
    private readonly SelfRoleReactionService selfRoles = selfRoles;
    private readonly GuildMembershipService memberships = memberships;
    private readonly ApplicationCommandRegistrar commands = commands;
    private readonly RankoonInteractionHandler interactions = interactions;
    private readonly ILogger<DiscordRuntimeEventDispatcher> logger = logger;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Attach("platform", BotIdentityMode.Rankoon, platform);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var runtimeId in bindings.Keys) Detach(runtimeId);
        return Task.CompletedTask;
    }

    public void Attach(string runtimeId, BotIdentityMode mode, DiscordShardedClient client, ulong? assignedGuildId = null)
    {
        var binding = new Binding(this, runtimeId, mode, client, assignedGuildId);
        if (!bindings.TryAdd(runtimeId, binding)) return;
        binding.Subscribe();
    }

    public void Detach(string runtimeId)
    {
        if (bindings.TryRemove(runtimeId, out var binding)) binding.Unsubscribe();
    }

    private bool Allows(string runtimeId, ulong? assignedGuildId, ulong guildId) =>
        (!assignedGuildId.HasValue || assignedGuildId.Value == guildId) && authority.IsAuthoritative(guildId, runtimeId);

    private sealed class Binding(DiscordRuntimeEventDispatcher owner, string runtimeId, BotIdentityMode mode, DiscordShardedClient client, ulong? assignedGuildId)
    {
        public void Subscribe()
        {
            client.MessageReceived += MessageAsync;
            client.ReactionAdded += ReactionAddedAsync;
            client.ReactionRemoved += ReactionRemovedAsync;
            client.ThreadCreated += ThreadAsync;
            client.GuildScheduledEventUserAdd += EventUserAddedAsync;
            client.GuildScheduledEventUserRemove += EventUserRemovedAsync;
            client.UserVoiceStateUpdated += VoiceAsync;
            client.ChannelDestroyed += ChannelDestroyedAsync;
            client.InteractionCreated += InteractionAsync;
            client.UserJoined += UserJoinedAsync;
            client.UserLeft += UserLeftAsync;
            client.ShardReady += ReadyAsync;
        }

        public void Unsubscribe()
        {
            client.MessageReceived -= MessageAsync;
            client.ReactionAdded -= ReactionAddedAsync;
            client.ReactionRemoved -= ReactionRemovedAsync;
            client.ThreadCreated -= ThreadAsync;
            client.GuildScheduledEventUserAdd -= EventUserAddedAsync;
            client.GuildScheduledEventUserRemove -= EventUserRemovedAsync;
            client.UserVoiceStateUpdated -= VoiceAsync;
            client.ChannelDestroyed -= ChannelDestroyedAsync;
            client.InteractionCreated -= InteractionAsync;
            client.UserJoined -= UserJoinedAsync;
            client.UserLeft -= UserLeftAsync;
            client.ShardReady -= ReadyAsync;
        }

        private Task MessageAsync(SocketMessage message) => message.Channel is SocketGuildChannel channel && Allowed(channel.Guild.Id) ? owner.activity.OnMessageAsync(message) : Task.CompletedTask;
        private Task ReactionAddedAsync(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction) => GuildId(channel, reaction) is { } guildId && Allowed(guildId) ? DispatchReactionAddedAsync(message, channel, reaction) : Task.CompletedTask;
        private Task ReactionRemovedAsync(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction) => GuildId(channel, reaction) is { } guildId && Allowed(guildId) ? DispatchReactionRemovedAsync(message, channel, reaction) : Task.CompletedTask;
        private async Task DispatchReactionAddedAsync(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction) { await owner.activity.OnReactionAsync(message, channel, reaction); await owner.selfRoles.OnReactionAddedAsync(client, message, channel, reaction); }
        private async Task DispatchReactionRemovedAsync(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction) { await owner.activity.OnReactionRemovedAsync(message, channel, reaction); await owner.selfRoles.OnReactionRemovedAsync(client, message, channel, reaction); }
        private Task ThreadAsync(SocketThreadChannel thread) => Allowed(thread.Guild.Id) ? owner.activity.OnThreadAsync(thread) : Task.CompletedTask;
        private Task EventUserAddedAsync(Cacheable<SocketUser, RestUser, IUser, ulong> user, SocketGuildEvent guildEvent) => Allowed(guildEvent.Guild.Id) ? owner.activity.OnEventInterestAsync(user, guildEvent) : Task.CompletedTask;
        private Task EventUserRemovedAsync(Cacheable<SocketUser, RestUser, IUser, ulong> user, SocketGuildEvent guildEvent) => Allowed(guildEvent.Guild.Id) ? owner.activity.OnEventInterestRemovedAsync(user, guildEvent) : Task.CompletedTask;
        private async Task VoiceAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
        {
            if (user is not SocketGuildUser member || !Allowed(member.Guild.Id)) return;
            await owner.voice.OnVoiceStateChangedAsync(user, before, after);
            await owner.hubs.OnVoiceStateChangedAsync(user, before, after);
        }
        private Task ChannelDestroyedAsync(SocketChannel channel) => channel is SocketGuildChannel guildChannel && Allowed(guildChannel.Guild.Id) ? owner.hubs.OnChannelDestroyedAsync(channel) : Task.CompletedTask;
        private Task InteractionAsync(SocketInteraction interaction) => interaction.GuildId is { } guildId && Allowed(guildId) ? owner.interactions.HandleAsync(interaction) : Task.CompletedTask;
        private Task UserJoinedAsync(SocketGuildUser user) => Allowed(user.Guild.Id) ? owner.memberships.UserJoinedAsync(user) : Task.CompletedTask;
        private Task UserLeftAsync(SocketGuild guild, SocketUser user) => Allowed(guild.Id) ? owner.memberships.UserLeftAsync(guild, user) : Task.CompletedTask;
        private async Task ReadyAsync(DiscordSocketClient shard)
        {
            foreach (var guild in shard.Guilds.Where(x => Allowed(x.Id)))
            {
                var runtime = new BotRuntimeContext(runtimeId, mode, client, guild);
                if (!await owner.commands.RegisterAsync(runtime)) owner.logger.LogWarning("Commands unavailable for runtime {RuntimeId} guild {GuildId}", runtimeId, guild.Id);
                await owner.selfRoles.OnGuildReadyAsync(guild);
                await owner.hubs.OnGuildReadyAsync(guild);
            }
        }

        private bool Allowed(ulong guildId) => owner.Allows(runtimeId, assignedGuildId, guildId);
        private static ulong? GuildId(Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction) =>
            reaction.User.IsSpecified && reaction.User.Value is SocketGuildUser member ? member.Guild.Id :
            channel.HasValue && channel.Value is SocketGuildChannel guildChannel ? guildChannel.Guild.Id : null;
    }
}
