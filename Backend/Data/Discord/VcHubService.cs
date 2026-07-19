using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Discord;

public sealed class VcHubService(DiscordShardedClient client, RankoonDbContext database, ILogger<VcHubService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hubReconciliationGates = new();
    private readonly ConcurrentDictionary<ulong, byte> _deletingHubChannels = new();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.UserVoiceStateUpdated += OnVoiceStateChangedAsync;
        client.ChannelDestroyed += OnChannelDestroyedAsync;
        client.ShardReady += OnShardReadyAsync;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CleanupAsync(stoppingToken);
                    await ReconcileAllHubsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Temporary voice channel cleanup failed; retrying in one minute");
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            client.UserVoiceStateUpdated -= OnVoiceStateChangedAsync;
            client.ChannelDestroyed -= OnChannelDestroyedAsync;
            client.ShardReady -= OnShardReadyAsync;
        }
    }

    private async Task OnShardReadyAsync(DiscordSocketClient shard)
    {
        try { await ReconcileHubsAsync(shard.Guilds); }
        catch (Exception exception) { logger.LogError(exception, "VC hub reconciliation failed after Discord shard {ShardId} became ready", shard.ShardId); }
    }

    private async Task OnChannelDestroyedAsync(SocketChannel channel)
    {
        if (channel is not SocketVoiceChannel voiceChannel || _deletingHubChannels.ContainsKey(voiceChannel.Id)) return;
        try
        {
            var hub = await database.VcHubs.Find(x => x.GuildId == voiceChannel.Guild.Id && x.JoinChannelId == voiceChannel.Id).FirstOrDefaultAsync();
            if (hub != null) await ReconcileHubAsync(voiceChannel.Guild, hub);
        }
        catch (Exception exception) { logger.LogError(exception, "VC hub reconciliation failed after channel {ChannelId} was deleted", voiceChannel.Id); }
    }

    private async Task OnVoiceStateChangedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        try
        {
            await HandleVoiceStateChangedAsync(user, before, after);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Temporary voice channel event failed for user {UserId}", user.Id);
        }
    }

    private async Task HandleVoiceStateChangedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (user.IsBot || user is not SocketGuildUser member || before.VoiceChannel?.Id == after.VoiceChannel?.Id) return;
        if (before.VoiceChannel != null) await DeleteIfEmptyAsync(member.Guild, before.VoiceChannel);
        if (after.VoiceChannel == null) return;
        var hub = await database.VcHubs.Find(x => x.GuildId == member.Guild.Id && x.JoinChannelId == after.VoiceChannel.Id && x.Enabled).FirstOrDefaultAsync();
        if (hub == null) return;
        var gate = _gates.GetOrAdd(hub.JoinChannelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var owned = await database.TemporaryVoiceChannels.CountDocumentsAsync(x => x.GuildId == member.Guild.Id && x.OwnerId == member.Id);
            if (owned >= hub.MaxChannelsPerOwner) return;
            var name = hub.NameTemplate.Replace("{username}", member.DisplayName, StringComparison.OrdinalIgnoreCase).Replace("{user}", member.Username, StringComparison.OrdinalIgnoreCase);
            var channel = await member.Guild.CreateVoiceChannelAsync(name, properties => { properties.CategoryId = hub.CategoryId; properties.UserLimit = hub.UserLimit; properties.Bitrate = hub.Bitrate; });
            await database.TemporaryVoiceChannels.InsertOneAsync(new TemporaryVoiceChannel { GuildId = member.Guild.Id, ChannelId = channel.Id, HubId = hub.Id!, OwnerId = member.Id });
            await member.ModifyAsync(properties => properties.Channel = channel);
            await database.GuildStats.UpdateOneAsync(x => x.GuildId == member.Guild.Id, Builders<GuildStats>.Update.SetOnInsert(x => x.GuildId, member.Guild.Id).Inc(x => x.TemporaryChannelsCreated, 1), new UpdateOptions { IsUpsert = true });
        }
        catch (Exception exception) { logger.LogError(exception, "Unable to create temporary voice channel for {UserId}", member.Id); }
        finally { gate.Release(); }
    }

    public async Task<bool> IsOwnerAsync(ulong guildId, ulong channelId, ulong userId) => await database.TemporaryVoiceChannels.Find(x => x.GuildId == guildId && x.ChannelId == channelId && x.OwnerId == userId).AnyAsync();
    public Task TransferOwnershipAsync(ulong guildId, ulong channelId, ulong ownerId) => database.TemporaryVoiceChannels.UpdateOneAsync(x => x.GuildId == guildId && x.ChannelId == channelId, Builders<TemporaryVoiceChannel>.Update.Set(x => x.OwnerId, ownerId));
    public async Task DeleteHubAsync(SocketGuild guild, VcHub hub, CancellationToken cancellationToken)
    {
        var gate = _hubReconciliationGates.GetOrAdd(hub.Id!, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var channel = guild.GetVoiceChannel(hub.JoinChannelId);
            if (channel != null)
            {
                _deletingHubChannels.TryAdd(channel.Id, 0);
                try
                {
                    try { await channel.DeleteAsync(); }
                    catch (global::Discord.Net.HttpException exception) when (exception.HttpCode == System.Net.HttpStatusCode.NotFound) { }
                    await database.VcHubs.DeleteOneAsync(x => x.Id == hub.Id, cancellationToken);
                }
                catch (global::Discord.Net.HttpException exception) { throw new InvalidOperationException("Der Hub-Kanal konnte nicht aus Discord geloescht werden.", exception); }
                finally { _deletingHubChannels.TryRemove(channel.Id, out _); }
                return;
            }

            await database.VcHubs.DeleteOneAsync(x => x.Id == hub.Id, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task ReconcileAllHubsAsync(CancellationToken cancellationToken) => await ReconcileHubsAsync(client.Guilds, cancellationToken);
    private async Task ReconcileHubsAsync(IEnumerable<SocketGuild> guilds, CancellationToken cancellationToken = default)
    {
        foreach (var guild in guilds)
        foreach (var hub in await database.VcHubs.Find(x => x.GuildId == guild.Id).ToListAsync(cancellationToken))
            await ReconcileHubAsync(guild, hub, cancellationToken);
    }

    private async Task ReconcileHubAsync(SocketGuild guild, VcHub hub, CancellationToken cancellationToken = default)
    {
        var gate = _hubReconciliationGates.GetOrAdd(hub.Id!, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var currentHub = await database.VcHubs.Find(x => x.Id == hub.Id).FirstOrDefaultAsync(cancellationToken);
            if (currentHub == null || guild.GetVoiceChannel(currentHub.JoinChannelId) != null) return;
            var channel = await guild.CreateVoiceChannelAsync(string.IsNullOrWhiteSpace(currentHub.HubChannelName) ? "VC erstellen" : currentHub.HubChannelName, options => options.CategoryId = currentHub.CategoryId);
            await database.VcHubs.UpdateOneAsync(x => x.Id == currentHub.Id, Builders<VcHub>.Update.Set(x => x.JoinChannelId, channel.Id), cancellationToken: cancellationToken);
            logger.LogInformation("Recreated VC hub channel {ChannelId} for hub {HubId}", channel.Id, currentHub.Id);
        }
        finally { gate.Release(); }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken) { foreach (var guild in client.Guilds) foreach (var channel in await database.TemporaryVoiceChannels.Find(x => x.GuildId == guild.Id).ToListAsync(cancellationToken)) { var socketChannel = guild.GetVoiceChannel(channel.ChannelId); if (socketChannel == null || socketChannel.ConnectedUsers.Count == 0) await DeleteIfEmptyAsync(guild, socketChannel, channel); } }
    private async Task DeleteIfEmptyAsync(SocketGuild guild, SocketVoiceChannel? channel, TemporaryVoiceChannel? record = null)
    {
        if (channel != null && channel.ConnectedUsers.Count > 0) return;
        record ??= channel == null
            ? null
            : await database.TemporaryVoiceChannels.Find(x => x.GuildId == guild.Id && x.ChannelId == channel.Id).FirstOrDefaultAsync();
        if (record == null) return;

        if (channel != null)
        {
            try
            {
                await channel.DeleteAsync();
            }
            catch (global::Discord.Net.HttpException exception) when (exception.HttpCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            {
                logger.LogWarning(exception, "Temporary voice channel {ChannelId} is no longer accessible; removing its database record", record.ChannelId);
            }
        }

        await database.TemporaryVoiceChannels.DeleteOneAsync(x => x.Id == record.Id);
    }
}
