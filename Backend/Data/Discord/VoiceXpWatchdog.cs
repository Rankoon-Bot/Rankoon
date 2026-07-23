using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using MongoDB.Driver;
using Microsoft.Extensions.Options;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Xp;
using Rankoon.Data.Reporting;

namespace Rankoon.Data.Discord;

public enum VoiceWatchdogState { Starting, Healthy, Degraded, Stale, Restarting, Faulted, Stopped }
public sealed record VoiceWatchdogStatus(ulong GuildId, VoiceWatchdogState State, DateTimeOffset? LastRunAt, DateTimeOffset? LastPersistenceAt, int ConnectedUsers, int EligibleUsers, int ExcludedUsers, string? LastError, int IntervalSeconds);

/// <summary>Rankoon-owned, per-guild voice reconciliation worker inspired by SharedVcWatchdog.</summary>
public sealed class VoiceXpWatchdog(IGuildDiscordContextResolver discord, RankoonDbContext database, IXpService xp, ServerBoosterXpMultiplierResolver boosterMultipliers, IReportWriter reports, TimeProvider timeProvider, IOptions<VoiceWatchdogOptions> options, ILogger<VoiceXpWatchdog> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<ulong, VoiceWatchdogStatus> _statuses = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _guildGates = new();
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(options.Value.IntervalSeconds);
    public VoiceWatchdogStatus GetStatus(ulong guildId) => _statuses.TryGetValue(guildId, out var status) ? status : new(guildId, VoiceWatchdogState.Stopped, null, null, 0, 0, 0, null, (int)_interval.TotalSeconds);

    public async Task ReconcileNowAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var guild = (await discord.ResolveAsync(guildId, cancellationToken))?.Guild ?? throw new InvalidOperationException("The guild is not available to the authoritative Discord runtime.");
        await ReconcileGuildAsync(guild, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var guildIds = await database.GuildXpSettings.Distinct(x => x.GuildId, Builders<GuildXpSettings>.Filter.Empty).ToListAsync(stoppingToken);
                foreach (var guildId in guildIds)
                    if (await discord.ResolveAsync(guildId, stoppingToken) is { } context)
                        await ReconcileGuildAsync(context.Guild, stoppingToken);
                await Task.Delay(_interval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { }
    }

    public async Task OnVoiceStateChangedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        try
        {
            await HandleVoiceStateChangedAsync(user, before, after);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Voice XP event failed for user {UserId}", user.Id);
            if (user is SocketGuildUser member) await reports.WriteErrorAsync(member.Guild.Id, "voice.xp.lifecycle", exception, user.Id, new Dictionary<string, object?> { ["userId"] = user.Id });
        }
    }

    private async Task HandleVoiceStateChangedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (user.IsBot || user is not SocketGuildUser member) return;
        var gate = _guildGates.GetOrAdd(member.Guild.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var settings = await xp.GetSettingsAsync(member.Guild.Id, CancellationToken.None);
            if (!settings.Enabled || !settings.Voice.Enabled)
            {
                await database.VoiceSessions.DeleteOneAsync(x => x.GuildId == member.Guild.Id && x.UserId == member.Id);
                return;
            }
            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (before.VoiceChannel?.Id != after.VoiceChannel?.Id)
            {
                if (before.VoiceChannel != null) await SettleUserAsync(member.Guild, member, before.VoiceChannel, now, CancellationToken.None);
                if (after.VoiceChannel != null)
                {
                    await StartSessionAsync(member.Guild.Id, member.Id, after.VoiceChannel.Id, now, CancellationToken.None);
                }
                else await database.VoiceSessions.DeleteOneAsync(x => x.GuildId == member.Guild.Id && x.UserId == member.Id);
            }
        }
        finally { gate.Release(); }
    }

    private async Task ReconcileGuildAsync(SocketGuild guild, CancellationToken cancellationToken)
    {
        var gate = _guildGates.GetOrAdd(guild.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { await ReconcileGuildCoreAsync(guild, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task ReconcileGuildCoreAsync(SocketGuild guild, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            var settings = await xp.GetSettingsAsync(guild.Id, cancellationToken);
            if (!settings.Enabled || !settings.Voice.Enabled)
            {
                await database.VoiceSessions.DeleteManyAsync(x => x.GuildId == guild.Id, cancellationToken);
                _statuses[guild.Id] = new(guild.Id, VoiceWatchdogState.Stopped, timeProvider.GetUtcNow(), null, 0, 0, 0, null, (int)_interval.TotalSeconds);
                return;
            }
            var connected = guild.VoiceChannels.SelectMany(x => x.ConnectedUsers).Where(x => !x.IsBot).DistinctBy(x => x.Id).ToArray();
            foreach (var member in connected)
            {
                if (member.VoiceChannel == null) continue;
                var session = await database.VoiceSessions.Find(x => x.GuildId == guild.Id && x.UserId == member.Id).FirstOrDefaultAsync(cancellationToken);
                if (session == null || session.ChannelId != member.VoiceChannel.Id)
                {
                    await StartSessionAsync(guild.Id, member.Id, member.VoiceChannel.Id, now, cancellationToken);
                }
                await SettleUserAsync(guild, member, member.VoiceChannel, now, cancellationToken);
            }
            var liveIds = connected.Select(x => x.Id).ToHashSet();
            await database.VoiceSessions.DeleteManyAsync(x => x.GuildId == guild.Id && !liveIds.Contains(x.UserId), cancellationToken);
            _statuses[guild.Id] = new(guild.Id, VoiceWatchdogState.Healthy, timeProvider.GetUtcNow(), timeProvider.GetUtcNow(), connected.Length, 0, 0, null, (int)_interval.TotalSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Voice watchdog failed for guild {GuildId}", guild.Id);
            await reports.WriteErrorAsync(guild.Id, "voice.watchdog", exception, metadata: new Dictionary<string, object?> { ["state"] = VoiceWatchdogState.Degraded });
            _statuses[guild.Id] = new(guild.Id, VoiceWatchdogState.Degraded, timeProvider.GetUtcNow(), null, 0, 0, 0, exception.GetBaseException().GetType().Name, (int)_interval.TotalSeconds);
        }
    }

    private async Task SettleUserAsync(SocketGuild guild, SocketGuildUser member, SocketVoiceChannel channel, DateTime now, CancellationToken cancellationToken)
    {
        var settings = await xp.GetSettingsAsync(guild.Id, cancellationToken);
        if (!settings.Enabled || !settings.Voice.Enabled) return;
        var session = await database.VoiceSessions.Find(x => x.GuildId == guild.Id && x.UserId == member.Id).FirstOrDefaultAsync(cancellationToken);
        if (session == null || now <= session.LastAccruedAt) return;
        var totalSeconds = (long)(now - session.JoinedAt).TotalSeconds;
        var excluded = settings.ExcludedChannelIds.Contains(channel.Id) || (channel.CategoryId.HasValue && settings.ExcludedCategoryIds.Contains(channel.CategoryId.Value)) || member.Roles.Any(x => settings.ExcludedRoleIds.Contains(x.Id)) || member.VoiceState is { IsDeafened: true } || (settings.Voice.ExcludeAfkChannel && guild.AFKChannel?.Id == channel.Id);
        var humans = channel.ConnectedUsers.Count(x => !x.IsBot && x.VoiceState is not { IsDeafened: true });
        var eligible = !excluded && (!settings.Voice.RequireMultipleHumans || humans > 1) && totalSeconds >= settings.Voice.MinimumSessionSeconds;
        // The first qualifying settlement books the whole session, including time before the minimum was reached.
        var periodStart = PeriodStart(session, eligible);
        if (eligible && now > periodStart)
        {
            var channelMultiplier = settings.ChannelMultipliers.FirstOrDefault(x => x.ChannelId == channel.Id)?.Multiplier ?? 1m;
            var award = boosterMultipliers.Apply("voice", settings.Voice.PointsPerMinute, channelMultiplier, settings, member);
            foreach (var (start, end) in await SegmentBySeasonBoundariesAsync(guild.Id, periodStart, now, cancellationToken))
            {
                var seconds = (long)(end - start).TotalSeconds;
                if (seconds == 0) continue;
                var amount = decimal.Round(seconds / 60m * award.Amount, 6, MidpointRounding.AwayFromZero);
                var request = new XpGrantRequest(guild.Id, member.Id, member.DisplayName, "voice", amount,
                    VoiceGrantKey(guild.Id, member.Id, channel.Id, start, end), start, channel.Id, start, end,
                    AppliedServerBoosterMultiplier: award.AppliedServerBoosterMultiplier);
                await xp.GrantAsync(request, cancellationToken);
            }
        }
        await database.VoiceSessions.UpdateOneAsync(x => x.GuildId == guild.Id && x.UserId == member.Id, Builders<VoiceSession>.Update.Set(x => x.LastAccruedAt, now).Inc(x => x.EligibleSeconds, eligible ? (long)(now - periodStart).TotalSeconds : 0), cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<(DateTime Start, DateTime End)>> SegmentBySeasonBoundariesAsync(ulong guildId, DateTime start, DateTime end, CancellationToken cancellationToken)
    {
        var boundaries = await database.GuildSeasons.Find(x => x.GuildId == guildId && x.StartsAtUtc < end && x.EndsAtUtc > start)
            .Project(x => new[] { x.StartsAtUtc, x.EndsAtUtc }).ToListAsync(cancellationToken);
        return CreateSegments(boundaries.SelectMany(x => x), start, end);
    }

    private static IReadOnlyList<(DateTime Start, DateTime End)> CreateSegments(IEnumerable<DateTime> boundaries, DateTime start, DateTime end)
    {
        var points = boundaries.Append(start).Append(end).Where(x => start <= x && x <= end).Distinct().Order().ToArray();
        return points.Zip(points.Skip(1), (segmentStart, segmentEnd) => (segmentStart, segmentEnd)).ToArray();
    }

    private static DateTime PeriodStart(VoiceSession session, bool eligible) => eligible && session.EligibleSeconds == 0 ? session.JoinedAt : session.LastAccruedAt;

    private static string VoiceGrantKey(ulong guildId, ulong userId, ulong channelId, DateTime start, DateTime end) => $"voice:{guildId}:{userId}:{channelId}:{start.Ticks}:{end.Ticks}";

    private Task StartSessionAsync(ulong guildId, ulong userId, ulong channelId, DateTime now, CancellationToken cancellationToken)
    {
        var update = Builders<VoiceSession>.Update
            .SetOnInsert(x => x.GuildId, guildId)
            .SetOnInsert(x => x.UserId, userId)
            .Set(x => x.ChannelId, channelId)
            .Set(x => x.JoinedAt, now)
            .Set(x => x.LastAccruedAt, now)
            .Set(x => x.EligibleSeconds, 0);
        return database.VoiceSessions.UpdateOneAsync(x => x.GuildId == guildId && x.UserId == userId, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }
}
