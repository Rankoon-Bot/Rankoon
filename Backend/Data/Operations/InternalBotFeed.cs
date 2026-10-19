using Discord;
using Discord.WebSocket;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Operations;

public static class InternalBotFeedConstants
{
    public const ulong SupportGuildId = 1528682065032249356;
    public const string SettingsId = "global";
}

public sealed class InternalBotFeedSettingsDocument
{
    [BsonId] public string Id { get; set; } = InternalBotFeedConstants.SettingsId;
    [BsonElement("enabled")] public bool Enabled { get; set; }
    [BsonElement("channel_id"), BsonIgnoreIfNull] public ulong? ChannelId { get; set; }
    [BsonElement("daily_summary_enabled")] public bool DailySummaryEnabled { get; set; }
    [BsonElement("daily_summary_hour_utc")] public int DailySummaryHourUtc { get; set; } = 0;
    [BsonElement("daily_summary_minute_utc")] public int DailySummaryMinuteUtc { get; set; } = 15;
    [BsonElement("revision")] public long Revision { get; set; } = 1;
    [BsonElement("updated_at_utc")] public DateTime UpdatedAtUtc { get; set; }
}

public sealed record InternalBotFeedChannelDto(string Id, string Name, bool BotCanSend);
public sealed record InternalBotFeedSettingsResponse(
    string SupportGuildId,
    string? SupportGuildName,
    bool SupportGuildAvailable,
    bool Enabled,
    string? ChannelId,
    bool DailySummaryEnabled,
    int DailySummaryHourUtc,
    int DailySummaryMinuteUtc,
    long Revision,
    DateTimeOffset? UpdatedAtUtc,
    IReadOnlyList<InternalBotFeedChannelDto> Channels);
public sealed record UpdateInternalBotFeedSettingsRequest(
    bool Enabled,
    string? ChannelId,
    bool DailySummaryEnabled,
    int DailySummaryHourUtc,
    int DailySummaryMinuteUtc,
    long Revision);

public sealed class InternalBotFeedSettingsStore(RankoonDbContext database, DiscordShardedClient discord, TimeProvider timeProvider)
{
    public async Task<InternalBotFeedSettingsDocument> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await database.InternalBotFeedSettings.Find(x => x.Id == InternalBotFeedConstants.SettingsId).FirstOrDefaultAsync(cancellationToken);
        if (settings != null) return settings;

        try
        {
            settings = new InternalBotFeedSettingsDocument { UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime };
            await database.InternalBotFeedSettings.InsertOneAsync(settings, cancellationToken: cancellationToken);
            return settings;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return await database.InternalBotFeedSettings.Find(x => x.Id == InternalBotFeedConstants.SettingsId).FirstAsync(cancellationToken);
        }
    }

    public async Task<InternalBotFeedSettingsDocument?> SaveAsync(UpdateInternalBotFeedSettingsRequest request, ulong channelId, CancellationToken cancellationToken)
    {
        var update = Builders<InternalBotFeedSettingsDocument>.Update
            .Set(x => x.Enabled, request.Enabled)
            .Set(x => x.ChannelId, channelId == 0 ? (ulong?)null : channelId)
            .Set(x => x.DailySummaryEnabled, request.DailySummaryEnabled)
            .Set(x => x.DailySummaryHourUtc, request.DailySummaryHourUtc)
            .Set(x => x.DailySummaryMinuteUtc, request.DailySummaryMinuteUtc)
            .Set(x => x.UpdatedAtUtc, timeProvider.GetUtcNow().UtcDateTime)
            .Inc(x => x.Revision, 1);
        return await database.InternalBotFeedSettings.FindOneAndUpdateAsync(
            x => x.Id == InternalBotFeedConstants.SettingsId && x.Revision == request.Revision,
            update,
            new FindOneAndUpdateOptions<InternalBotFeedSettingsDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
    }

    public InternalBotFeedSettingsResponse ToResponse(InternalBotFeedSettingsDocument settings)
    {
        var guild = discord.GetGuild(InternalBotFeedConstants.SupportGuildId);
        var channels = guild?.TextChannels.Select(channel =>
        {
            var permissions = guild.CurrentUser?.GetPermissions(channel);
            return new InternalBotFeedChannelDto(channel.Id.ToString(), channel.Name,
                permissions is { ViewChannel: true, SendMessages: true });
        }).OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        return new(
            InternalBotFeedConstants.SupportGuildId.ToString(),
            guild?.Name,
            guild != null,
            settings.Enabled,
            settings.ChannelId?.ToString(),
            settings.DailySummaryEnabled,
            settings.DailySummaryHourUtc,
            settings.DailySummaryMinuteUtc,
            settings.Revision,
            settings.UpdatedAtUtc == default ? null : new DateTimeOffset(DateTime.SpecifyKind(settings.UpdatedAtUtc, DateTimeKind.Utc)),
            channels);
    }

    public bool CanBotSendTo(ulong channelId)
    {
        var guild = discord.GetGuild(InternalBotFeedConstants.SupportGuildId);
        var channel = guild?.GetTextChannel(channelId);
        var permissions = channel != null && guild?.CurrentUser is { } currentUser ? currentUser.GetPermissions(channel) : null;
        return channel != null && permissions is { ViewChannel: true, SendMessages: true };
    }

    public bool SupportGuildAvailable => discord.GetGuild(InternalBotFeedConstants.SupportGuildId) != null;
}

public sealed class InternalBotFeedDelivery
{
    [BsonId] public string Id { get; set; } = string.Empty;
    [BsonElement("kind")] public string Kind { get; set; } = string.Empty;
    [BsonElement("payload")] public string Payload { get; set; } = string.Empty;
    [BsonElement("created_at_utc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("period_start_utc"), BsonIgnoreIfNull] public DateTime? PeriodStartUtc { get; set; }
    [BsonElement("status")] public string Status { get; set; } = InternalBotFeedStatuses.Pending;
    [BsonElement("next_attempt_at_utc")] public DateTime NextAttemptAtUtc { get; set; }
    [BsonElement("lease_id"), BsonIgnoreIfNull] public string? LeaseId { get; set; }
    [BsonElement("lease_expires_at_utc"), BsonIgnoreIfNull] public DateTime? LeaseExpiresAtUtc { get; set; }
    [BsonElement("attempts")] public int Attempts { get; set; }
    [BsonElement("sent_at_utc"), BsonIgnoreIfNull] public DateTime? SentAtUtc { get; set; }
    [BsonElement("expires_at_utc")] public DateTime ExpiresAtUtc { get; set; }
}

public static class InternalBotFeedStatuses
{
    public const string Pending = "Pending";
    public const string Leased = "Leased";
    public const string Sent = "Sent";
    public const string Cancelled = "Cancelled";
}

/// <summary>Queues privacy-safe operational messages for the configured Rankoon support channel.</summary>
public sealed class InternalBotFeedService(
    RankoonDbContext database,
    DiscordShardedClient discord,
    InternalBotFeedSettingsStore settingsStore,
    TimeProvider timeProvider,
    ILogger<InternalBotFeedService> logger) : BackgroundService
{
    private const int SentDeliveryRetentionDays = 400;
    private const int PollIntervalSeconds = 15;
    private const int RetryDelaySeconds = 60;
    private const int LeaseSeconds = 120;

    public Task GuildInstalledAsync(ulong botId, SocketGuild guild) => QueueGuildEventIfEnabledAsync("installed", botId, guild);

    public Task GuildRemovedAsync(ulong botId, SocketGuild guild) => QueueGuildEventIfEnabledAsync("removed", botId, guild);

    private async Task QueueGuildEventIfEnabledAsync(string eventName, ulong botId, SocketGuild guild)
    {
        try
        {
            var settings = await settingsStore.GetAsync();
            if (settings.Enabled && settings.ChannelId is > 0) await EnqueueGuildEventAsync(eventName, botId, guild);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not queue internal feed event {EventName} for guild {GuildId}", eventName, guild.Id);
        }
    }

    private async Task EnqueueGuildEventAsync(string eventName, ulong botId, SocketGuild guild)
    {
        if (botId == 0) return;

        var now = timeProvider.GetUtcNow();
        var joinedAt = guild.CurrentUser?.JoinedAt;
        var id = joinedAt is { } knownJoin
            ? $"guild:{eventName}:{BotGuildHistoryRecorder.InstallationId(botId, guild.Id, knownJoin)}"
            : $"guild:{eventName}:{botId}:{guild.Id}:{now.UtcTicks}";
        var occurredAt = eventName == "installed" && joinedAt is { } installedAt ? installedAt : now;
        var safeName = CleanGuildName(guild.Name);
        var verb = eventName == "installed" ? "Bot auf Server installiert" : "Bot von Server entfernt";
        var marker = eventName == "installed" ? "🟢" : "⚪";
        var content = $"{marker} **{verb}**\nServer: {safeName} (`{guild.Id}`)\nZeitpunkt: {occurredAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC";

        await InsertOnceAsync(new InternalBotFeedDelivery
        {
            Id = id,
            Kind = "GuildMembership",
            Payload = content,
            CreatedAtUtc = now.UtcDateTime,
            NextAttemptAtUtc = now.UtcDateTime,
            ExpiresAtUtc = now.AddDays(SentDeliveryRetentionDays).UtcDateTime
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await settingsStore.GetAsync(stoppingToken);
                await CancelDisabledPendingAsync(settings, stoppingToken);
                if (HasAnyOutput(settings))
                {
                    await EnqueueDueDailySummariesAsync(settings, stoppingToken);
                    var delivery = await ClaimNextAsync(stoppingToken);
                    if (delivery != null) await DeliverAsync(delivery, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Internal Discord feed worker iteration failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static bool HasAnyOutput(InternalBotFeedSettingsDocument settings) =>
        settings.ChannelId is > 0 && (settings.Enabled || settings.DailySummaryEnabled);

    private async Task CancelDisabledPendingAsync(InternalBotFeedSettingsDocument settings, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var filters = Builders<InternalBotFeedDelivery>.Filter;
        var status = filters.In(x => x.Status, [InternalBotFeedStatuses.Pending, InternalBotFeedStatuses.Leased]);
        var disabledKinds = new List<FilterDefinition<InternalBotFeedDelivery>>();
        if (!settings.Enabled) disabledKinds.Add(filters.Eq(x => x.Kind, "GuildMembership"));
        if (!settings.DailySummaryEnabled) disabledKinds.Add(filters.Eq(x => x.Kind, "DailySummary"));
        if (HasAnyOutput(settings) && disabledKinds.Count == 0) return;
        var pending = HasAnyOutput(settings) ? filters.And(status, filters.Or(disabledKinds.ToArray())) : status;
        await database.InternalBotFeedDeliveries.UpdateManyAsync(pending,
            Builders<InternalBotFeedDelivery>.Update
                .Set(x => x.Status, InternalBotFeedStatuses.Cancelled)
                .Set(x => x.ExpiresAtUtc, now.AddDays(SentDeliveryRetentionDays))
                .Unset(x => x.LeaseId)
                .Unset(x => x.LeaseExpiresAtUtc),
            cancellationToken: cancellationToken);
    }

    private async Task EnqueueDueDailySummariesAsync(InternalBotFeedSettingsDocument settings, CancellationToken cancellationToken)
    {
        if (!settings.DailySummaryEnabled || settings.ChannelId is not > 0) return;
        if (discord.ConnectionState != ConnectionState.Connected || discord.GetGuild(InternalBotFeedConstants.SupportGuildId) == null) return;

        var now = timeProvider.GetUtcNow();
        if (now.TimeOfDay < new TimeSpan(settings.DailySummaryHourUtc, settings.DailySummaryMinuteUtc, 0)) return;

        var today = DateTime.SpecifyKind(now.UtcDateTime.Date, DateTimeKind.Utc);
        var periodStart = today.AddDays(-1);
        var id = $"daily:{periodStart:yyyy-MM-dd}";
        if (await database.InternalBotFeedDeliveries.Find(x => x.Id == id).AnyAsync(cancellationToken)) return;
        var payload = await BuildDailySummaryAsync(periodStart, today, cancellationToken);
        await InsertOnceAsync(new InternalBotFeedDelivery
        {
            Id = id,
            Kind = "DailySummary",
            Payload = payload,
            CreatedAtUtc = now.UtcDateTime,
            PeriodStartUtc = periodStart,
            NextAttemptAtUtc = now.UtcDateTime,
            ExpiresAtUtc = now.AddDays(SentDeliveryRetentionDays).UtcDateTime
        }, cancellationToken);
    }

    private async Task<string> BuildDailySummaryAsync(DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var rows = await database.GuildAnalyticsBuckets.Aggregate()
            .Match(x => x.Granularity == GuildAnalyticsGranularity.Day && x.Metric == GuildAnalyticsMetric.EventCount && x.BucketStartUtc >= from && x.BucketStartUtc < to)
            .Group(x => x.GuildId, guild => new DailyGuildAggregate(
                guild.Key,
                guild.Sum(bucket => bucket.Count),
                guild.Where(bucket => bucket.Feature == GuildAnalyticsFeature.Commands).Sum(bucket => bucket.Count)))
            .Limit(100_000)
            .ToListAsync(cancellationToken);

        var installs = await database.BotGuildInstallations.CountDocumentsAsync(
            x => x.Identity == "Rankoon" && x.JoinObserved && x.JoinedAtUtc >= from && x.JoinedAtUtc < to,
            cancellationToken: cancellationToken);
        var removals = await database.BotGuildInstallations.CountDocumentsAsync(
            x => x.Identity == "Rankoon" && x.RemovalEvidence == "observed" && x.RemovedAtUtc >= from && x.RemovedAtUtc < to,
            cancellationToken: cancellationToken);

        var activeGuilds = rows.LongCount(x => x.EventCount > 0);
        var eventCount = rows.Sum(x => x.EventCount);
        var commandCount = rows.Sum(x => x.CommandCount);
        return $"📊 **Rankoon Tagesübersicht — {from:yyyy-MM-dd} UTC**\n" +
            $"Installationen: {installs} · Entfernungen: {removals}\n" +
            $"Aktive Server: {activeGuilds} · Aktuell installierte Server: {discord.Guilds.Count}\n" +
            $"Erfasste Events: {eventCount} · Commands: {commandCount}";
    }

    private async Task InsertOnceAsync(InternalBotFeedDelivery delivery, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await database.InternalBotFeedDeliveries.InsertOneAsync(delivery, cancellationToken: cancellationToken);
                return;
            }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                // Stable IDs make gateway replays and multiple service instances idempotent.
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (attempt == 3)
                {
                    logger.LogError(exception, "Could not queue internal Discord feed item {FeedItemId}", delivery.Id);
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt), timeProvider, cancellationToken);
            }
        }
    }

    private async Task<InternalBotFeedDelivery?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var filters = Builders<InternalBotFeedDelivery>.Filter;
        var ready = filters.Or(
            filters.And(filters.Eq(x => x.Status, InternalBotFeedStatuses.Pending), filters.Lte(x => x.NextAttemptAtUtc, now)),
            filters.And(filters.Eq(x => x.Status, InternalBotFeedStatuses.Leased), filters.Lte(x => x.LeaseExpiresAtUtc, now)));
        var leaseId = Guid.NewGuid().ToString("N");
        var update = Builders<InternalBotFeedDelivery>.Update
            .Set(x => x.Status, InternalBotFeedStatuses.Leased)
            .Set(x => x.LeaseId, leaseId)
            .Set(x => x.LeaseExpiresAtUtc, now.AddSeconds(LeaseSeconds))
            .Inc(x => x.Attempts, 1);
        return await database.InternalBotFeedDeliveries.FindOneAndUpdateAsync(
            ready,
            update,
            new FindOneAndUpdateOptions<InternalBotFeedDelivery>
            {
                ReturnDocument = ReturnDocument.After,
                Sort = Builders<InternalBotFeedDelivery>.Sort.Ascending(x => x.CreatedAtUtc)
            },
            cancellationToken);
    }

    private async Task DeliverAsync(InternalBotFeedDelivery delivery, CancellationToken cancellationToken)
    {
        try
        {
            var settings = await settingsStore.GetAsync(cancellationToken);
            var shouldDeliver = delivery.Kind switch
            {
                "GuildMembership" => settings.Enabled,
                "DailySummary" => settings.DailySummaryEnabled,
                _ => false
            };
            if (!shouldDeliver || settings.ChannelId is not > 0)
            {
                await CancelClaimedAsync(delivery, cancellationToken);
                return;
            }

            var isStillLeased = await database.InternalBotFeedDeliveries.Find(x => x.Id == delivery.Id && x.LeaseId == delivery.LeaseId && x.Status == InternalBotFeedStatuses.Leased).AnyAsync(cancellationToken);
            if (!isStillLeased) return;

            var channel = discord.GetChannel(settings.ChannelId!.Value) as SocketTextChannel;
            if (channel == null || channel.Guild.Id != InternalBotFeedConstants.SupportGuildId)
                throw new InvalidOperationException("Configured feed channel is unavailable or is not in the configured support server.");

            await channel.SendMessageAsync(delivery.Payload, allowedMentions: AllowedMentions.None);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            await database.InternalBotFeedDeliveries.UpdateOneAsync(
                x => x.Id == delivery.Id && x.LeaseId == delivery.LeaseId && x.Status == InternalBotFeedStatuses.Leased,
                Builders<InternalBotFeedDelivery>.Update
                    .Set(x => x.Status, InternalBotFeedStatuses.Sent)
                    .Set(x => x.SentAtUtc, now)
                    .Set(x => x.ExpiresAtUtc, now.AddDays(SentDeliveryRetentionDays))
                    .Unset(x => x.LeaseId)
                    .Unset(x => x.LeaseExpiresAtUtc),
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The lease expires and another worker can safely recover this item after restart.
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Internal Discord feed delivery {FeedItemId} failed; it will be retried", delivery.Id);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            await database.InternalBotFeedDeliveries.UpdateOneAsync(
                x => x.Id == delivery.Id && x.LeaseId == delivery.LeaseId && x.Status == InternalBotFeedStatuses.Leased,
                Builders<InternalBotFeedDelivery>.Update
                    .Set(x => x.Status, InternalBotFeedStatuses.Pending)
                    .Set(x => x.NextAttemptAtUtc, now.AddSeconds(RetryDelaySeconds))
                    .Unset(x => x.LeaseId)
                    .Unset(x => x.LeaseExpiresAtUtc),
                cancellationToken: CancellationToken.None);
        }
    }

    private Task CancelClaimedAsync(InternalBotFeedDelivery delivery, CancellationToken cancellationToken) =>
        database.InternalBotFeedDeliveries.UpdateOneAsync(
            x => x.Id == delivery.Id && x.LeaseId == delivery.LeaseId && x.Status == InternalBotFeedStatuses.Leased,
            Builders<InternalBotFeedDelivery>.Update
                .Set(x => x.Status, InternalBotFeedStatuses.Cancelled)
                .Set(x => x.ExpiresAtUtc, timeProvider.GetUtcNow().UtcDateTime.AddDays(SentDeliveryRetentionDays))
                .Unset(x => x.LeaseId)
                .Unset(x => x.LeaseExpiresAtUtc),
            cancellationToken: cancellationToken);

    private static string CleanGuildName(string name)
    {
        var clean = new string(name.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (clean.Length > 100) clean = clean[..100];
        return string.IsNullOrWhiteSpace(clean) ? "Unbekannter Server" : clean;
    }

    private sealed record DailyGuildAggregate(ulong GuildId, long EventCount, long CommandCount);
}
