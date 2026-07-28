using Discord.WebSocket;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;

namespace Rankoon.Data.Xp;

public interface IGuildUserPresentationService
{
    Task<IReadOnlyDictionary<ulong, string?>> ResolveIconUrlsAsync(ulong guildId, IEnumerable<ulong> userIds, CancellationToken cancellationToken = default);
}

// This service only reads the socket cache. REST hydration is intentionally delegated to a worker.
public sealed class GuildUserPresentationService(
    IGuildDiscordContextResolver discord,
    IGuildUserAvatarCacheRepository cache,
    IGuildUserAvatarUrlFactory urls,
    TimeProvider timeProvider,
    ILogger<GuildUserPresentationService> logger) : IGuildUserPresentationService
{
    public static string CreateDefaultAvatarUrl(ulong userId) => new GuildUserAvatarUrlFactory().CreateDefaultAvatarUrl(userId);

    public async Task<IReadOnlyDictionary<ulong, string?>> ResolveIconUrlsAsync(ulong guildId, IEnumerable<ulong> userIds, CancellationToken cancellationToken = default)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<ulong, string?>();
        IReadOnlyDictionary<ulong, GuildUserAvatarCacheEntry> persisted = new Dictionary<ulong, GuildUserAvatarCacheEntry>();
        try { persisted = await cache.GetManyAsync(guildId, ids, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogError(exception, "Unable to load avatar cache for guild {GuildId}", guildId); }

        GuildDiscordContext? context = null;
        try { context = await discord.ResolveAsync(guildId, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogWarning(exception, "Unable to resolve Discord context for guild {GuildId}", guildId); }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updates = new List<GuildUserAvatarCacheEntry>();
        var missing = new List<ulong>();
        var result = new Dictionary<ulong, string?>(ids.Length);
        foreach (var userId in ids)
        {
            var socketUser = context?.Guild.GetUser(userId);
            if (socketUser != null)
            {
                var observed = GuildUserAvatarObservation.Create(socketUser, now);
                result[userId] = urls.CreateUrl(guildId, userId, observed.AvatarId, observed.GuildAvatarId, observed.DefaultAvatarIndex);
                persisted.TryGetValue(userId, out var existing);
                if (existing == null || !GuildUserAvatarObservation.Matches(existing, observed)) updates.Add(GuildUserAvatarObservation.Apply(existing, observed, now));
                continue;
            }
            if (persisted.TryGetValue(userId, out var entry)) result[userId] = urls.CreateUrl(guildId, userId, entry.AvatarId, entry.GuildAvatarId, entry.DefaultAvatarIndex);
            else { result[userId] = urls.CreateDefaultAvatarUrl(userId); missing.Add(userId); }
        }

        try
        {
            if (missing.Count > 0) await cache.EnsureHydrationRequestedAsync(guildId, missing, cancellationToken);
            if (updates.Count > 0) await cache.BulkUpsertAsync(updates, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Unable to persist {SocketUpdates} avatar updates and request {HydrationRequests} hydrations for guild {GuildId}", updates.Count, missing.Count, guildId);
        }
        return result;
    }
}
