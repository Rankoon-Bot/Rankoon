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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.UserVoiceStateUpdated += OnVoiceStateChangedAsync;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CleanupAsync(stoppingToken);
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
        finally { client.UserVoiceStateUpdated -= OnVoiceStateChangedAsync; }
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
    private async Task CleanupAsync(CancellationToken cancellationToken) { foreach (var guild in client.Guilds) foreach (var channel in await database.TemporaryVoiceChannels.Find(x => x.GuildId == guild.Id).ToListAsync(cancellationToken)) { var socketChannel = guild.GetVoiceChannel(channel.ChannelId); if (socketChannel == null || socketChannel.ConnectedUsers.Count == 0) await DeleteIfEmptyAsync(guild, socketChannel, channel); } }
    private async Task DeleteIfEmptyAsync(SocketGuild guild, SocketVoiceChannel? channel, TemporaryVoiceChannel? record = null) { if (channel != null && channel.ConnectedUsers.Count > 0) return; record ??= await database.TemporaryVoiceChannels.Find(x => x.GuildId == guild.Id && x.ChannelId == channel!.Id).FirstOrDefaultAsync(); if (record == null) return; if (channel != null) await channel.DeleteAsync(); await database.TemporaryVoiceChannels.DeleteOneAsync(x => x.Id == record.Id); }
}
