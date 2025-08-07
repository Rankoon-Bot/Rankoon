using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Xp;

namespace Rankoon.Data.Discord;

public enum VoiceWatchdogState { Starting, Healthy, Degraded, Stale, Restarting, Faulted, Stopped }
public sealed record VoiceWatchdogStatus(ulong GuildId, VoiceWatchdogState State, DateTimeOffset? LastRunAt, DateTimeOffset? LastPersistenceAt, int ConnectedUsers, int EligibleUsers, int ExcludedUsers, string? LastError);

/// <summary>Rankoon-owned, per-guild voice reconciliation worker inspired by SharedVcWatchdog.</summary>
public sealed class VoiceXpWatchdog(DiscordShardedClient client, RankoonDbContext database, IXpService xp, LevelRoleService levelRoles, TimeProvider timeProvider, ILogger<VoiceXpWatchdog> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<ulong, VoiceWatchdogStatus> _statuses = new();
    public VoiceWatchdogStatus GetStatus(ulong guildId) => _statuses.TryGetValue(guildId, out var status) ? status : new(guildId, VoiceWatchdogState.Stopped, null, null, 0, 0, 0, null);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.UserVoiceStateUpdated += OnVoiceStateChangedAsync;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var guild in client.Guilds) await ReconcileGuildAsync(guild, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
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
            logger.LogError(exception, "Voice XP event failed for user {UserId}", user.Id);
        }
    }

    private async Task HandleVoiceStateChangedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (user.IsBot || user is not SocketGuildUser member) return;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (before.VoiceChannel?.Id != after.VoiceChannel?.Id)
        {
            if (before.VoiceChannel != null) await SettleUserAsync(member.Guild, member, before.VoiceChannel, now, CancellationToken.None);
            if (after.VoiceChannel != null)
            {
                await database.VoiceSessions.ReplaceOneAsync(x => x.GuildId == member.Guild.Id && x.UserId == member.Id,
                    new VoiceSession { GuildId = member.Guild.Id, UserId = member.Id, ChannelId = after.VoiceChannel.Id, JoinedAt = now, LastAccruedAt = now }, new ReplaceOptions { IsUpsert = true });
            }
            else await database.VoiceSessions.DeleteOneAsync(x => x.GuildId == member.Guild.Id && x.UserId == member.Id);
        }
    }

    private async Task ReconcileGuildAsync(SocketGuild guild, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            var settings = await xp.GetSettingsAsync(guild.Id, cancellationToken);
            if (!settings.Enabled || !settings.Voice.Enabled) { _statuses[guild.Id] = new(guild.Id, VoiceWatchdogState.Stopped, DateTimeOffset.UtcNow, null, 0, 0, 0, null); return; }
            var connected = guild.VoiceChannels.SelectMany(x => x.ConnectedUsers).Where(x => !x.IsBot).DistinctBy(x => x.Id).ToArray();
            foreach (var member in connected)
            {
                if (member.VoiceChannel == null) continue;
                var session = await database.VoiceSessions.Find(x => x.GuildId == guild.Id && x.UserId == member.Id).FirstOrDefaultAsync(cancellationToken);
                if (session == null || session.ChannelId != member.VoiceChannel.Id)
                {
                    session = new VoiceSession { GuildId = guild.Id, UserId = member.Id, ChannelId = member.VoiceChannel.Id, JoinedAt = now, LastAccruedAt = now };
                    await database.VoiceSessions.ReplaceOneAsync(x => x.GuildId == guild.Id && x.UserId == member.Id, session, new ReplaceOptions { IsUpsert = true }, cancellationToken);
                }
                await SettleUserAsync(guild, member, member.VoiceChannel, now, cancellationToken);
            }
            var liveIds = connected.Select(x => x.Id).ToHashSet();
            await database.VoiceSessions.DeleteManyAsync(x => x.GuildId == guild.Id && !liveIds.Contains(x.UserId), cancellationToken);
            _statuses[guild.Id] = new(guild.Id, VoiceWatchdogState.Healthy, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, connected.Length, 0, 0, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Voice watchdog failed for guild {GuildId}", guild.Id);
            _statuses[guild.Id] = new(guild.Id, VoiceWatchdogState.Degraded, DateTimeOffset.UtcNow, null, 0, 0, 0, exception.Message);
        }
    }

    private async Task SettleUserAsync(SocketGuild guild, SocketGuildUser member, SocketVoiceChannel channel, DateTime now, CancellationToken cancellationToken)
    {
        var settings = await xp.GetSettingsAsync(guild.Id, cancellationToken);
        var session = await database.VoiceSessions.Find(x => x.GuildId == guild.Id && x.UserId == member.Id).FirstOrDefaultAsync(cancellationToken);
        if (session == null || now <= session.LastAccruedAt) return;
        var seconds = Math.Min((long)(now - session.LastAccruedAt).TotalSeconds, settings.Voice.CheckIntervalSeconds * 2L);
        var totalSeconds = (long)(now - session.JoinedAt).TotalSeconds;
        var excluded = settings.ExcludedChannelIds.Contains(channel.Id) || (channel.CategoryId.HasValue && settings.ExcludedCategoryIds.Contains(channel.CategoryId.Value)) || member.Roles.Any(x => settings.ExcludedRoleIds.Contains(x.Id)) || member.VoiceState is { IsDeafened: true } || (settings.Voice.ExcludeAfkChannel && guild.AFKChannel?.Id == channel.Id);
        var humans = channel.ConnectedUsers.Count(x => !x.IsBot && x.VoiceState is not { IsDeafened: true });
        var eligible = !excluded && (!settings.Voice.RequireMultipleHumans || humans > 1) && totalSeconds >= settings.Voice.MinimumSessionSeconds;
        if (eligible && seconds > 0)
        {
            var multiplier = settings.ChannelMultipliers.FirstOrDefault(x => x.ChannelId == channel.Id)?.Multiplier ?? 1m;
            var amount = seconds / 60m * settings.Voice.PointsPerMinute * multiplier;
            if (await xp.GrantAsync(guild.Id, member.Id, member.DisplayName, "voice", amount, $"voice:{guild.Id}:{member.Id}:{channel.Id}:{session.LastAccruedAt.Ticks}:{now.Ticks}", channel.Id, cancellationToken)) await levelRoles.SynchronizeAsync(guild.Id, member.Id, cancellationToken);
        }
        await database.VoiceSessions.UpdateOneAsync(x => x.GuildId == guild.Id && x.UserId == member.Id, Builders<VoiceSession>.Update.Set(x => x.LastAccruedAt, now).Inc(x => x.EligibleSeconds, eligible ? seconds : 0), cancellationToken: cancellationToken);
        await database.MemberXp.UpdateOneAsync(x => x.GuildId == guild.Id && x.UserId == member.Id, Builders<MemberXp>.Update.SetOnInsert(x => x.GuildId, guild.Id).SetOnInsert(x => x.UserId, member.Id).Set(x => x.DisplayName, member.DisplayName).Inc(x => x.VoiceSeconds, seconds), new UpdateOptions { IsUpsert = true }, cancellationToken);
    }
}
