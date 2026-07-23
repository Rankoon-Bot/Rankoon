using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;

namespace Rankoon.Data.Discord;

public sealed class VcHubService(IGuildDiscordContextResolver discord, RankoonDbContext database, IReportWriter reports, TimeProvider timeProvider, ILogger<VcHubService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ownerGates = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _temporaryChannelGates = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hubReconciliationGates = new();
    private readonly ConcurrentDictionary<ulong, byte> _deletingHubChannels = new();
    private readonly ConcurrentDictionary<ulong, byte> _deletingTemporaryChannels = new();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

                await Task.Delay(TimeSpan.FromMinutes(1), timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { }
    }

    public async Task OnShardReadyAsync(DiscordSocketClient shard)
    {
        try { await ReconcileHubsAsync(shard.Guilds); }
        catch (Exception exception) { logger.LogError(exception, "VC hub reconciliation failed after Discord shard {ShardId} became ready", shard.ShardId); }
    }

    public async Task OnChannelDestroyedAsync(SocketChannel channel)
    {
        if (channel is not SocketVoiceChannel voiceChannel) return;
        try
        {
            if (!_deletingTemporaryChannels.ContainsKey(voiceChannel.Id))
            {
                var temporary = await database.TemporaryVoiceChannels.Find(x => x.GuildId == voiceChannel.Guild.Id && x.ChannelId == voiceChannel.Id).FirstOrDefaultAsync();
                if (temporary != null)
                {
                    var gate = _temporaryChannelGates.GetOrAdd(temporary.ChannelId, _ => new SemaphoreSlim(1, 1));
                    await gate.WaitAsync();
                    try
                    {
                        var result = await database.TemporaryVoiceChannels.DeleteOneAsync(x => x.Id == temporary.Id);
                        if (result.DeletedCount == 1)
                            await WriteReportAsync(new(voiceChannel.Guild.Id, ReportCategories.Activity, ReportNames.VoiceChannelDeleted, ReportOutcomes.Succeeded,
                                Metadata: new Dictionary<string, object?> { ["channelId"] = temporary.ChannelId, ["hubId"] = temporary.HubId }));
                    }
                    finally { gate.Release(); }
                }
            }

            if (_deletingHubChannels.ContainsKey(voiceChannel.Id)) return;
            var hub = await database.VcHubs.Find(x => x.GuildId == voiceChannel.Guild.Id && x.JoinChannelId == voiceChannel.Id).FirstOrDefaultAsync();
            if (hub != null) await ReconcileHubAsync(voiceChannel.Guild, hub);
        }
        catch (Exception exception) { logger.LogError(exception, "VC hub reconciliation failed after channel {ChannelId} was deleted", voiceChannel.Id); }
    }

    public async Task OnVoiceStateChangedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        try
        {
            await HandleVoiceStateChangedAsync(user, before, after);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Temporary voice channel event failed for user {UserId}", user.Id);
            if (user is SocketGuildUser member) await WriteErrorAsync(member.Guild.Id, "voice.channel.lifecycle", exception, member.Id, new Dictionary<string, object?> { ["userId"] = member.Id });
        }
    }

    private async Task HandleVoiceStateChangedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (user.IsBot || user is not SocketGuildUser member || before.VoiceChannel?.Id == after.VoiceChannel?.Id) return;
        if (before.VoiceChannel != null) await DeleteIfEmptyAsync(member.Guild, before.VoiceChannel);
        if (after.VoiceChannel == null) return;
        var hub = await database.VcHubs.Find(x => x.GuildId == member.Guild.Id && x.JoinChannelId == after.VoiceChannel.Id && x.Enabled).FirstOrDefaultAsync();
        if (hub == null) return;
        var ownerGate = _ownerGates.GetOrAdd($"{member.Guild.Id}:{member.Id}", _ => new SemaphoreSlim(1, 1));
        await ownerGate.WaitAsync();
        var gate = _gates.GetOrAdd(hub.JoinChannelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (member.VoiceChannel?.Id != hub.JoinChannelId || member.Guild.GetVoiceChannel(hub.JoinChannelId) == null) return;
            var currentHub = await database.VcHubs.Find(x => x.Id == hub.Id && x.Enabled && x.JoinChannelId == hub.JoinChannelId).FirstOrDefaultAsync();
            if (currentHub == null) return;
            hub = currentHub;
            var owned = await database.TemporaryVoiceChannels.CountDocumentsAsync(x => x.GuildId == member.Guild.Id && x.OwnerId == member.Id);
            if (owned >= hub.MaxChannelsPerOwner) return;
            var name = hub.NameTemplate.Replace("{username}", member.DisplayName, StringComparison.OrdinalIgnoreCase).Replace("{user}", member.Username, StringComparison.OrdinalIgnoreCase);
            var channel = await member.Guild.CreateVoiceChannelAsync(name, properties => { properties.CategoryId = hub.CategoryId; properties.UserLimit = hub.UserLimit; properties.Bitrate = hub.Bitrate; });
            var record = new TemporaryVoiceChannel { GuildId = member.Guild.Id, ChannelId = channel.Id, HubId = hub.Id!, OwnerId = member.Id, CreatedAt = timeProvider.GetUtcNow().UtcDateTime };
            try
            {
                await database.TemporaryVoiceChannels.InsertOneAsync(record);
            }
            catch (Exception exception)
            {
                await CompensateTemporaryChannelAsync(member.Guild, channel, null, exception);
                throw;
            }

            if (member.VoiceChannel?.Id != hub.JoinChannelId)
            {
                var exception = new InvalidOperationException("The user left the VC hub before the temporary channel could be joined.");
                await CompensateTemporaryChannelAsync(member.Guild, channel, record, exception);
                throw exception;
            }

            try
            {
                await member.ModifyAsync(properties => properties.Channel = channel);
            }
            catch (Exception exception) when (!IsMemberInChannel(member, channel))
            {
                await CompensateTemporaryChannelAsync(member.Guild, channel, record, exception);
                throw;
            }

            try
            {
                await database.GuildStats.UpdateOneAsync(x => x.GuildId == member.Guild.Id, Builders<GuildStats>.Update.SetOnInsert(x => x.GuildId, member.Guild.Id).Inc(x => x.TemporaryChannelsCreated, 1), new UpdateOptions { IsUpsert = true });
            }
            catch (Exception exception) { await WriteErrorAsync(member.Guild.Id, "voice.channel.stats", exception, member.Id, new Dictionary<string, object?> { ["channelId"] = channel.Id, ["hubId"] = hub.Id }); }
            await WriteReportAsync(new(member.Guild.Id, ReportCategories.Activity, ReportNames.VoiceChannelCreated, ReportOutcomes.Succeeded, ActorId: member.Id, Metadata: new Dictionary<string, object?> { ["channelId"] = channel.Id, ["hubId"] = hub.Id }));
        }
        catch (Exception exception) { logger.LogError(exception, "Unable to create temporary voice channel for {UserId}", member.Id); await WriteErrorAsync(member.Guild.Id, "voice.channel.create", exception, member.Id, new Dictionary<string, object?> { ["userId"] = member.Id, ["hubId"] = hub.Id }); }
        finally { gate.Release(); ownerGate.Release(); }
    }

    private static bool IsMemberInChannel(SocketGuildUser member, IVoiceChannel channel) => member.VoiceChannel?.Id == channel.Id;

    private async Task CompensateTemporaryChannelAsync(SocketGuild guild, IVoiceChannel channel, TemporaryVoiceChannel? record, Exception cause)
    {
        var deleted = false;
        _deletingTemporaryChannels.TryAdd(channel.Id, 0);
        try
        {
            await channel.DeleteAsync();
            deleted = true;
        }
        catch (global::Discord.Net.HttpException exception) when (exception.HttpCode == System.Net.HttpStatusCode.NotFound) { deleted = true; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to delete orphaned temporary voice channel {ChannelId}", channel.Id);
            await WriteErrorAsync(guild.Id, "voice.channel.compensate.discord", exception, record?.OwnerId, new Dictionary<string, object?> { ["channelId"] = channel.Id, ["hubId"] = record?.HubId, ["cause"] = cause.GetType().Name });
        }
        finally { _deletingTemporaryChannels.TryRemove(channel.Id, out _); }
        if (!deleted) return;

        try
        {
            await database.TemporaryVoiceChannels.DeleteOneAsync(x => x.GuildId == guild.Id && x.ChannelId == channel.Id);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to remove partial temporary voice channel record {ChannelId}", channel.Id);
            await WriteErrorAsync(guild.Id, "voice.channel.compensate.database", exception, record?.OwnerId, new Dictionary<string, object?> { ["channelId"] = channel.Id, ["hubId"] = record?.HubId, ["cause"] = cause.GetType().Name });
        }
    }

    private async Task WriteReportAsync(ReportWrite report)
    {
        try { await reports.WriteAsync(report); }
        catch (Exception exception) { logger.LogError(exception, "Unable to write voice hub report {ReportName} for guild {GuildId}", report.Name, report.GuildId); }
    }

    private async Task WriteErrorAsync(ulong guildId, string source, Exception exception, ulong? actorId = null, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        try { await reports.WriteErrorAsync(guildId, source, exception, actorId, metadata); }
        catch (Exception reportException) { logger.LogError(reportException, "Unable to write voice hub error report {Source} for guild {GuildId}", source, guildId); }
    }

    public async Task<bool> IsOwnerAsync(ulong guildId, ulong channelId, ulong userId) => await database.TemporaryVoiceChannels.Find(x => x.GuildId == guildId && x.ChannelId == channelId && x.OwnerId == userId).AnyAsync();
    public async Task TransferOwnershipAsync(ulong guildId, ulong channelId, ulong actorId, ulong ownerId)
    {
        var result = await database.TemporaryVoiceChannels.UpdateOneAsync(x => x.GuildId == guildId && x.ChannelId == channelId && x.OwnerId == actorId, Builders<TemporaryVoiceChannel>.Update.Set(x => x.OwnerId, ownerId));
        if (result.MatchedCount != 1) throw new InvalidOperationException("The temporary voice channel no longer exists.");
        await WriteReportAsync(new(guildId, ReportCategories.Activity, ReportNames.VoiceOwnershipTransferred, ReportOutcomes.Succeeded, ActorId: actorId,
            Metadata: new Dictionary<string, object?> { ["channelId"] = channelId, ["targetId"] = ownerId }, SubjectId: ownerId, ChannelId: channelId));
    }
    public async Task DeleteHubAsync(SocketGuild guild, VcHub hub, CancellationToken cancellationToken)
    {
        var gate = _hubReconciliationGates.GetOrAdd(hub.Id!, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var channel = hub.IsManagedChannel ? guild.GetVoiceChannel(hub.JoinChannelId) : null;
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

    public async Task UpdateHubAsync(SocketGuild guild, VcHub existingHub, VcHub updatedHub, CancellationToken cancellationToken)
    {
        var gate = _hubReconciliationGates.GetOrAdd(existingHub.Id!, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (updatedHub.JoinChannelId == 0)
            {
                var channel = await guild.CreateVoiceChannelAsync(string.IsNullOrWhiteSpace(updatedHub.HubChannelName) ? "VC erstellen" : updatedHub.HubChannelName, options => options.CategoryId = updatedHub.CategoryId);
                updatedHub.JoinChannelId = channel.Id;
                updatedHub.IsManagedChannel = true;
            }
            else if (updatedHub.JoinChannelId == existingHub.JoinChannelId)
            {
                updatedHub.IsManagedChannel = existingHub.IsManagedChannel;
                if (existingHub.IsManagedChannel && existingHub.CategoryId != updatedHub.CategoryId && guild.GetVoiceChannel(existingHub.JoinChannelId) is { } channel)
                    await channel.ModifyAsync(options => options.CategoryId = updatedHub.CategoryId);
            }
            else
            {
                updatedHub.IsManagedChannel = false;
            }

            if (existingHub.IsManagedChannel && existingHub.JoinChannelId != updatedHub.JoinChannelId && guild.GetVoiceChannel(existingHub.JoinChannelId) is { } previousChannel)
            {
                _deletingHubChannels.TryAdd(previousChannel.Id, 0);
                try { await previousChannel.DeleteAsync(); }
                finally { _deletingHubChannels.TryRemove(previousChannel.Id, out _); }
            }

            await database.VcHubs.ReplaceOneAsync(x => x.GuildId == updatedHub.GuildId && x.Id == updatedHub.Id, updatedHub, cancellationToken: cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task ReconcileAllHubsAsync(CancellationToken cancellationToken)
    {
        var guildIds = await database.VcHubs.Distinct(x => x.GuildId, Builders<VcHub>.Filter.Empty).ToListAsync(cancellationToken);
        foreach (var guildId in guildIds)
            if (await discord.ResolveAsync(guildId, cancellationToken) is { } context)
                await ReconcileHubsAsync([context.Guild], cancellationToken);
    }
    public async Task OnGuildReadyAsync(SocketGuild guild)
    {
        try { await ReconcileHubsAsync([guild]); }
        catch (Exception exception) { logger.LogError(exception, "VC hub reconciliation failed for guild {GuildId}", guild.Id); }
    }
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
            await database.VcHubs.UpdateOneAsync(x => x.Id == currentHub.Id, Builders<VcHub>.Update.Set(x => x.JoinChannelId, channel.Id).Set(x => x.IsManagedChannel, true), cancellationToken: cancellationToken);
            logger.LogInformation("Recreated VC hub channel {ChannelId} for hub {HubId}", channel.Id, currentHub.Id);
        }
        finally { gate.Release(); }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var guildIds = await database.TemporaryVoiceChannels.Distinct(x => x.GuildId, Builders<TemporaryVoiceChannel>.Filter.Empty).ToListAsync(cancellationToken);
        foreach (var guildId in guildIds)
        {
            if (await discord.ResolveAsync(guildId, cancellationToken) is not { } context) continue;
            foreach (var channel in await database.TemporaryVoiceChannels.Find(x => x.GuildId == guildId).ToListAsync(cancellationToken))
            {
                var socketChannel = context.Guild.GetVoiceChannel(channel.ChannelId);
                if (socketChannel == null || socketChannel.ConnectedUsers.Count == 0) await DeleteIfEmptyAsync(context.Guild, socketChannel, channel);
            }
        }
    }
    private async Task DeleteIfEmptyAsync(SocketGuild guild, SocketVoiceChannel? channel, TemporaryVoiceChannel? record = null)
    {
        record ??= channel == null
            ? null
            : await database.TemporaryVoiceChannels.Find(x => x.GuildId == guild.Id && x.ChannelId == channel.Id).FirstOrDefaultAsync();
        if (record == null) return;

        var gate = _temporaryChannelGates.GetOrAdd(record.ChannelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            channel ??= guild.GetVoiceChannel(record.ChannelId);
            if (channel != null && channel.ConnectedUsers.Count > 0) return;

            var deleted = channel == null;
            if (channel != null)
            {
                _deletingTemporaryChannels.TryAdd(channel.Id, 0);
                try
                {
                    await channel.DeleteAsync();
                    deleted = true;
                }
                catch (global::Discord.Net.HttpException exception) when (exception.HttpCode == System.Net.HttpStatusCode.NotFound)
                {
                    logger.LogWarning(exception, "Temporary voice channel {ChannelId} is no longer accessible; removing its database record", record.ChannelId);
                    deleted = true;
                }
                catch (global::Discord.Net.HttpException exception) when (exception.HttpCode == System.Net.HttpStatusCode.Forbidden)
                {
                    logger.LogWarning(exception, "Temporary voice channel {ChannelId} cannot be deleted because permissions are missing", record.ChannelId);
                    await WriteErrorAsync(guild.Id, "voice.channel.delete", exception, record.OwnerId, new Dictionary<string, object?> { ["channelId"] = record.ChannelId, ["hubId"] = record.HubId });
                }
                finally { _deletingTemporaryChannels.TryRemove(channel.Id, out _); }
            }

            if (!deleted) return;
            await database.TemporaryVoiceChannels.DeleteOneAsync(x => x.Id == record.Id);
            await WriteReportAsync(new(guild.Id, ReportCategories.Activity, ReportNames.VoiceChannelDeleted, ReportOutcomes.Succeeded, Metadata: new Dictionary<string, object?> { ["channelId"] = record.ChannelId, ["hubId"] = record.HubId }));
        }
        finally { gate.Release(); }
    }
}
