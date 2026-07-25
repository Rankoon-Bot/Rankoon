using System.Collections.Concurrent;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Rankoon.Data.Analytics;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Operations;
using Rankoon.Data.Xp;

namespace Rankoon.Data.Discord;

public enum VoiceWatchdogState { Starting, Healthy, Degraded, Stale, Restarting, Faulted, Stopped }
public sealed record VoiceWatchdogStatus(ulong GuildId, VoiceWatchdogState State, DateTimeOffset? LastRunAt, DateTimeOffset? LastPersistenceAt, int ConnectedUsers, int EligibleUsers, int ExcludedUsers, string? LastError, int IntervalSeconds);

public sealed class VoiceXpWatchdog(IGuildDiscordContextResolver discord, RankoonDbContext database, IXpService xp, IVoiceActivityAccumulator voiceActivity, IVoiceActivityProjectionService projection, ServerBoosterXpMultiplierResolver boosterMultipliers, IGuildAnalyticsRecorder analytics, IOperationalErrorRecorder errors, IWorkerHealthRegistry health, TimeProvider timeProvider, IOptions<VoiceWatchdogOptions> options, ILogger<VoiceXpWatchdog> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<ulong, VoiceWatchdogStatus> statuses = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> guildGates = new();
    private readonly TimeSpan interval = TimeSpan.FromSeconds(options.Value.IntervalSeconds);
    public VoiceWatchdogStatus GetStatus(ulong guildId) => statuses.TryGetValue(guildId, out var status) ? status : new(guildId, VoiceWatchdogState.Stopped, null, null, 0, 0, 0, null, (int)interval.TotalSeconds);

    public async Task ReconcileNowAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var guild = (await discord.ResolveAsync(guildId, cancellationToken))?.Guild ?? throw new InvalidOperationException("The guild is not available to the authoritative Discord runtime.");
        await ReconcileGuildAsync(guild, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var guildIds = await database.GuildXpSettings.Distinct(x => x.GuildId, Builders<GuildXpSettings>.Filter.Empty).ToListAsync(stoppingToken);
                foreach (var guildId in guildIds)
                    if (await discord.ResolveAsync(guildId, stoppingToken) is { } context) await ReconcileGuildAsync(context.Guild, stoppingToken);
                failures = 0;
                health.Report("voice-xp-watchdog", WorkerHealthState.Healthy);
                await Task.Delay(interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                failures++;
                logger.LogError(exception, "Voice XP watchdog loop failed; continuing");
                health.Report("voice-xp-watchdog", WorkerHealthState.Degraded, exception.GetType().Name);
                try { await errors.RecordAsync(new(exception, "worker", "voice.watchdog.loop", Worker: "voice-xp-watchdog"), stoppingToken); }
                catch (Exception recordingException) when (recordingException is not OperationCanceledException) { logger.LogWarning(recordingException, "Could not record voice watchdog loop failure"); }
                var delay = TimeSpan.FromSeconds(Math.Min(300, interval.TotalSeconds * Math.Max(1, failures)));
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
        }
    }

    public async Task OnVoiceStateChangedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        try { await HandleVoiceStateChangedAsync(user, before, after); }
        catch (Exception exception)
        {
            logger.LogError(exception, "Voice XP event failed for user {UserId}", user.Id);
            if (user is SocketGuildUser member) await errors.RecordAsync(new(exception, "discord", "voice.xp.lifecycle", GuildId: member.Guild.Id, ActorUserId: user.Id, Worker: "voice-xp-watchdog"));
        }
    }

    private async Task HandleVoiceStateChangedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (user.IsBot || user is not SocketGuildUser member) return;
        var channelChanged = before.VoiceChannel?.Id != after.VoiceChannel?.Id;
        if (!channelChanged && before.IsDeafened == after.IsDeafened) return;
        var gate = guildGates.GetOrAdd(member.Guild.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var settings = await xp.GetSettingsAsync(member.Guild.Id);
            var session = await database.VoiceSessions.Find(x => x.GuildId == member.Guild.Id && x.UserId == member.Id).FirstOrDefaultAsync();
            if (!IsVoiceXpEnabled(settings))
            {
                if (session != null)
                {
                    await projection.ProjectPendingAsync(member.Guild.Id, member.Id, member.DisplayName, immediate: true);
                    await database.VoiceSessions.DeleteOneAsync(x => x.Id == session.Id);
                }
                return;
            }
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var seasons = await LoadSeasonsAsync(member.Guild.Id, session?.JoinedAt ?? now, now);
            if (before.VoiceChannel != null && session?.ChannelId == before.VoiceChannel.Id)
                await SettleUserAsync(member.Guild, member, before.VoiceChannel, before.IsDeafened, settings, session, seasons, now, CancellationToken.None, immediateProjection: true);
            if (!channelChanged) return;
            if (after.VoiceChannel != null)
                await StartSessionAsync(member.Guild.Id, member.Id, after.VoiceChannel.Id, now, CancellationToken.None);
            else
                await database.VoiceSessions.DeleteOneAsync(x => x.GuildId == member.Guild.Id && x.UserId == member.Id);
        }
        finally { gate.Release(); }
    }

    private async Task ReconcileGuildAsync(SocketGuild guild, CancellationToken cancellationToken)
    {
        var gate = guildGates.GetOrAdd(guild.Id, _ => new SemaphoreSlim(1, 1));
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
            if (!IsVoiceXpEnabled(settings))
            {
                var disabledSessions = await database.VoiceSessions.Find(x => x.GuildId == guild.Id).ToListAsync(cancellationToken);
                foreach (var session in disabledSessions)
                    await projection.ProjectPendingAsync(guild.Id, session.UserId, guild.GetUser(session.UserId)?.DisplayName, cancellationToken, immediate: true);
                await database.VoiceSessions.DeleteManyAsync(x => x.GuildId == guild.Id, cancellationToken);
                statuses[guild.Id] = new(guild.Id, VoiceWatchdogState.Stopped, timeProvider.GetUtcNow(), null, 0, 0, 0, null, (int)interval.TotalSeconds);
                return;
            }

            var connected = guild.VoiceChannels.SelectMany(x => x.ConnectedUsers).Where(x => !x.IsBot).DistinctBy(x => x.Id).ToArray();
            var sessions = (await database.VoiceSessions.Find(x => x.GuildId == guild.Id).ToListAsync(cancellationToken)).ToDictionary(x => x.UserId);
            var earliest = sessions.Count == 0 ? now : sessions.Values.Min(x => x.JoinedAt);
            var seasons = await LoadSeasonsAsync(guild.Id, earliest, now, cancellationToken);
            var eligibleCount = 0;
            var excludedCount = 0;
            foreach (var member in connected)
            {
                var channel = member.VoiceChannel;
                if (channel == null) continue;
                if (!sessions.TryGetValue(member.Id, out var session) || session.ChannelId != channel.Id || string.IsNullOrWhiteSpace(session.SessionId))
                {
                    session = NewSession(guild.Id, member.Id, channel.Id, now);
                    await UpsertSessionAsync(session, cancellationToken);
                    sessions[member.Id] = session;
                }
                var outcome = await SettleUserAsync(guild, member, channel, member.VoiceState?.IsDeafened == true, settings, session, seasons, now, cancellationToken);
                if (outcome) eligibleCount++; else excludedCount++;
            }
            var liveIds = connected.Select(x => x.Id).ToHashSet();
            await database.VoiceSessions.DeleteManyAsync(x => x.GuildId == guild.Id && !liveIds.Contains(x.UserId), cancellationToken);
            statuses[guild.Id] = new(guild.Id, VoiceWatchdogState.Healthy, timeProvider.GetUtcNow(), timeProvider.GetUtcNow(), connected.Length, eligibleCount, excludedCount, null, (int)interval.TotalSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Voice watchdog failed for guild {GuildId}", guild.Id);
            health.Report("voice-xp-watchdog", WorkerHealthState.Degraded, exception.GetType().Name);
            await errors.RecordAsync(new(exception, "worker", "voice.watchdog", GuildId: guild.Id, Worker: "voice-xp-watchdog", Context: new Dictionary<string, object?> { ["state"] = VoiceWatchdogState.Degraded }));
            statuses[guild.Id] = new(guild.Id, VoiceWatchdogState.Degraded, timeProvider.GetUtcNow(), null, 0, 0, 0, exception.GetBaseException().GetType().Name, (int)interval.TotalSeconds);
        }
    }

    private async Task<bool> SettleUserAsync(SocketGuild guild, SocketGuildUser member, SocketVoiceChannel channel, bool isDeafened, GuildXpSettings settings, VoiceSession session, IReadOnlyList<GuildSeason> seasons, DateTime now, CancellationToken cancellationToken, bool immediateProjection = false)
    {
        if (now <= session.LastAccruedAt) return false;
        var excluded = settings.ExcludedChannelIds.Contains(channel.Id) || (channel.CategoryId.HasValue && settings.ExcludedCategoryIds.Contains(channel.CategoryId.Value)) ||
            member.Roles.Any(x => settings.ExcludedRoleIds.Contains(x.Id)) || isDeafened || (settings.Voice.ExcludeAfkChannel && guild.AFKChannel?.Id == channel.Id);
        var humans = channel.ConnectedUsers.Count(x => !x.IsBot && x.VoiceState is not { IsDeafened: true });
        var qualifies = !excluded && (!settings.Voice.RequireMultipleHumans || humans > 1);
        var eligibilityStart = EligibilityStart(session);
        var eligible = qualifies && (long)(now - eligibilityStart).TotalSeconds >= settings.Voice.MinimumSessionSeconds;
        var periodStart = PeriodStart(session, eligible);
        if (eligible && now > periodStart)
        {
            var channelMultiplier = settings.ChannelMultipliers.FirstOrDefault(x => x.ChannelId == channel.Id)?.Multiplier ?? 1m;
            var award = boosterMultipliers.Apply("voice", settings.Voice.PointsPerMinute, channelMultiplier, settings, member);
            var intervals = CreateSegments(SeasonBoundaries(seasons).Concat(EachUtcMidnight(periodStart, now)), periodStart, now);
            var totalEligibleSeconds = 0L;
            var intervalAward = 0m;
            foreach (var (start, end) in intervals)
            {
                var seconds = (long)(end - start).TotalSeconds;
                if (seconds <= 0) continue;
                var amount = RoundAccrual(seconds, award.Amount);
                totalEligibleSeconds += seconds;
                intervalAward += amount;
                var season = seasons.SingleOrDefault(x => x.StartsAtUtc <= start && start < x.EndsAtUtc);
                await voiceActivity.AccrueAsync(new(guild.Id, member.Id, session.SessionId, start, end, channel.Id, season?.Id, seconds, amount,
                    award.Amount, channelMultiplier, award.AppliedServerBoosterMultiplier > 1m ? award.AppliedServerBoosterMultiplier : null, settings.Revision, now, member.DisplayName), cancellationToken);
            }
            await projection.ProjectPendingAsync(guild.Id, member.Id, member.DisplayName, cancellationToken, immediateProjection || intervals.Count > 1);
            analytics.TryRecord(new(guild.Id, GuildAnalyticsMetric.Quantity, (long)decimal.Round(intervalAward, 0), GuildAnalyticsFeature.Voice, GuildAnalyticsOutcome.Succeeded, "xp.grant", "voice", ChannelId: channel.Id, DurationSeconds: totalEligibleSeconds, OccurredAt: periodStart));
        }
        else if (!eligible)
        {
            var reason = excluded ? "excluded" : humans <= 1 && settings.Voice.RequireMultipleHumans ? "insufficientParticipants" : "minimumDuration";
            analytics.TryRecord(new(guild.Id, GuildAnalyticsMetric.EventCount, Feature: GuildAnalyticsFeature.Voice, Outcome: GuildAnalyticsOutcome.Skipped, Operation: "voice.qualification", Source: "voice", Reason: reason, ChannelId: channel.Id, OccurredAt: new DateTimeOffset(now)));
        }

        var sessionUpdate = Builders<VoiceSession>.Update.Set(x => x.LastAccruedAt, now)
            .Inc(x => x.EligibleSeconds, eligible ? (long)(now - periodStart).TotalSeconds : 0).Inc(x => x.Revision, 1);
        if (!qualifies) sessionUpdate = sessionUpdate.Set(x => x.EligibilityStartedAt, now);
        await database.VoiceSessions.UpdateOneAsync(x => x.GuildId == session.GuildId && x.UserId == session.UserId && x.SessionId == session.SessionId && x.Revision == session.Revision,
            sessionUpdate, cancellationToken: cancellationToken);
        session.LastAccruedAt = now;
        if (!qualifies) session.EligibilityStartedAt = now;
        if (eligible) session.EligibleSeconds += (long)(now - periodStart).TotalSeconds;
        session.Revision++;
        return eligible;
    }

    internal static decimal RoundAccrual(long seconds, decimal effectiveXpPerMinute) => decimal.Round(seconds / 60m * effectiveXpPerMinute, 6, MidpointRounding.AwayFromZero);
    private static IEnumerable<DateTime> SeasonBoundaries(IEnumerable<GuildSeason> seasons) => seasons.SelectMany(x => new[] { x.StartsAtUtc, x.EndsAtUtc });
    private async Task<IReadOnlyList<GuildSeason>> LoadSeasonsAsync(ulong guildId, DateTime start, DateTime end, CancellationToken cancellationToken = default) =>
        await database.GuildSeasons.Find(x => x.GuildId == guildId && x.StartsAtUtc < end && x.EndsAtUtc > start).ToListAsync(cancellationToken);
    private static IReadOnlyList<(DateTime Start, DateTime End)> CreateSegments(IEnumerable<DateTime> boundaries, DateTime start, DateTime end) => VoiceActivityAccumulator.Split(start, end, boundaries).Select(x => (x.StartsAtUtc, x.EndsAtUtc)).ToArray();
    private static IEnumerable<DateTime> EachUtcMidnight(DateTime start, DateTime end) { for (var value = start.Date.AddDays(1); value < end; value = value.AddDays(1)) yield return DateTime.SpecifyKind(value, DateTimeKind.Utc); }
    private static DateTime EligibilityStart(VoiceSession session) => session.EligibilityStartedAt ?? session.JoinedAt;
    private static DateTime PeriodStart(VoiceSession session, bool eligible) => eligible && session.EligibleSeconds == 0 ? EligibilityStart(session) : session.LastAccruedAt;
    private static bool IsVoiceXpEnabled(GuildXpSettings settings) => settings.Enabled && settings.Voice.Enabled;

    private Task StartSessionAsync(ulong guildId, ulong userId, ulong channelId, DateTime now, CancellationToken cancellationToken) => UpsertSessionAsync(NewSession(guildId, userId, channelId, now), cancellationToken);
    private static VoiceSession NewSession(ulong guildId, ulong userId, ulong channelId, DateTime now) => new() { GuildId = guildId, UserId = userId, ChannelId = channelId, SessionId = Guid.NewGuid().ToString("N"), JoinedAt = now, EligibilityStartedAt = now, LastAccruedAt = now };
    private Task UpsertSessionAsync(VoiceSession session, CancellationToken cancellationToken) => database.VoiceSessions.UpdateOneAsync(x => x.GuildId == session.GuildId && x.UserId == session.UserId,
        Builders<VoiceSession>.Update.SetOnInsert(x => x.GuildId, session.GuildId).SetOnInsert(x => x.UserId, session.UserId).Set(x => x.ChannelId, session.ChannelId).Set(x => x.SessionId, session.SessionId)
            .Set(x => x.JoinedAt, session.JoinedAt).Set(x => x.EligibilityStartedAt, session.EligibilityStartedAt).Set(x => x.LastAccruedAt, session.LastAccruedAt).Set(x => x.EligibleSeconds, 0).Set(x => x.Revision, 0), new UpdateOptions { IsUpsert = true }, cancellationToken);
}
