using Discord;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;

namespace Rankoon.Data.Xp;

public sealed class GuildUserAvatarHydrationOptions
{
    public const string SectionName = "GuildUserAvatarHydration";
    public int BatchSize { get; set; } = 50;
    public int MaxConcurrency { get; set; } = 2;
    public int PollSeconds { get; set; } = 15;
    public int LeaseSeconds { get; set; } = 120;
    public static bool IsValid(GuildUserAvatarHydrationOptions value) => value.BatchSize is >= 1 and <= 100 && value.MaxConcurrency is >= 1 and <= 4 && value.PollSeconds is >= 1 and <= 300 && value.LeaseSeconds is >= 30 and <= 900;
}

public sealed class GuildUserAvatarHydrationWorker(IGuildUserAvatarCacheRepository cache, IGuildDiscordContextResolver discord, TimeProvider timeProvider, IOptions<GuildUserAvatarHydrationOptions> configuredOptions, ILogger<GuildUserAvatarHydrationWorker> logger) : BackgroundService
{
    private readonly GuildUserAvatarHydrationOptions options = configuredOptions.Value;
    private readonly string workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds));
        do
        {
            try
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;
                var batch = await cache.ClaimHydrationBatchAsync(workerId, options.BatchSize, now, TimeSpan.FromSeconds(options.LeaseSeconds), stoppingToken);
                if (batch.Count > 0) await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = options.MaxConcurrency, CancellationToken = stoppingToken }, HydrateAsync);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogError(exception, "Avatar hydration worker iteration failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async ValueTask HydrateAsync(GuildUserAvatarCacheEntry entry, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            var context = await discord.ResolveAsync(entry.GuildId, cancellationToken);
            if (context == null) { await RetryAsync(entry, now, cancellationToken); return; }
            try
            {
                var member = await context.Client.Rest.GetGuildUserAsync(entry.GuildId, entry.UserId, new RequestOptions { CancelToken = cancellationToken });
                if (member != null) { await CompleteAsync(entry, member.AvatarId, member.GuildAvatarId, now, cancellationToken); return; }
            }
            catch (global::Discord.Net.HttpException exception) when (exception.HttpCode == System.Net.HttpStatusCode.NotFound) { }
            var user = await context.Client.Rest.GetUserAsync(entry.UserId, new RequestOptions { CancelToken = cancellationToken });
            if (user != null) { await CompleteAsync(entry, user.AvatarId, null, now, cancellationToken); return; }
            await RetryAsync(entry, now, cancellationToken, true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Avatar hydration failed for user {UserId} in guild {GuildId}", entry.UserId, entry.GuildId);
            await RetryAsync(entry, now, cancellationToken);
        }
    }

    private Task CompleteAsync(GuildUserAvatarCacheEntry entry, string? avatarId, string? guildAvatarId, DateTime now, CancellationToken cancellationToken) => cache.BulkUpsertAsync([new()
    {
        Id = entry.Id, GuildId = entry.GuildId, UserId = entry.UserId, AvatarId = avatarId, GuildAvatarId = guildAvatarId, DefaultAvatarIndex = entry.DefaultAvatarIndex ?? (byte)((entry.UserId >> 22) % 6), UpdatedAtUtc = now, LastObservedAtUtc = entry.LastObservedAtUtc, LastHydratedAtUtc = now, NeedsHydration = false
    }], cancellationToken);

    private Task RetryAsync(GuildUserAvatarCacheEntry entry, DateTime now, CancellationToken cancellationToken, bool notFound = false)
    {
        var attempts = entry.HydrationAttemptCount + 1;
        var minutes = Math.Min(notFound ? 1440 : 360, (int)Math.Pow(2, Math.Min(attempts, 10)) * (notFound ? 60 : 1));
        return cache.BulkUpsertAsync([new GuildUserAvatarCacheEntry
        {
            Id = entry.Id, GuildId = entry.GuildId, UserId = entry.UserId, AvatarId = entry.AvatarId, GuildAvatarId = entry.GuildAvatarId, DefaultAvatarIndex = entry.DefaultAvatarIndex, UpdatedAtUtc = entry.UpdatedAtUtc, LastObservedAtUtc = entry.LastObservedAtUtc, LastHydratedAtUtc = entry.LastHydratedAtUtc, NeedsHydration = true, HydrationAttemptCount = attempts, NextHydrationAttemptAtUtc = now.AddMinutes(minutes)
        }], cancellationToken);
    }
}
