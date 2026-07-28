using System.Collections.Concurrent;
using System.Threading.Channels;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Rankoon.Data.Model;

namespace Rankoon.Data.Xp;

public sealed record ObservedGuildUserAvatar(ulong GuildId, ulong UserId, string? AvatarId, string? GuildAvatarId, byte DefaultAvatarIndex, DateTime ObservedAtUtc);

public interface IGuildUserAvatarObserver { void Observe(SocketGuildUser user); }

public static class GuildUserAvatarObservation
{
    public static ObservedGuildUserAvatar Create(SocketGuildUser user, DateTime observedAtUtc) => new(user.Guild.Id, user.Id, user.AvatarId, user.GuildAvatarId, DefaultAvatarIndex(user.Id), observedAtUtc);
    public static bool Matches(GuildUserAvatarCacheEntry entry, ObservedGuildUserAvatar observed) => entry.AvatarId == observed.AvatarId && entry.GuildAvatarId == observed.GuildAvatarId && entry.DefaultAvatarIndex == observed.DefaultAvatarIndex;
    public static GuildUserAvatarCacheEntry Apply(GuildUserAvatarCacheEntry? existing, ObservedGuildUserAvatar observed, DateTime nowUtc) => new()
    {
        Id = existing?.Id, GuildId = observed.GuildId, UserId = observed.UserId, AvatarId = observed.AvatarId, GuildAvatarId = observed.GuildAvatarId,
        DefaultAvatarIndex = observed.DefaultAvatarIndex, UpdatedAtUtc = existing != null && Matches(existing, observed) ? existing.UpdatedAtUtc : nowUtc,
        LastObservedAtUtc = observed.ObservedAtUtc, LastHydratedAtUtc = existing?.LastHydratedAtUtc, NeedsHydration = false,
        HydrationAttemptCount = 0
    };

    // Discord's snowflake-derived default index is authoritative for modern discriminator-less users.
    private static byte DefaultAvatarIndex(ulong userId) => (byte)((userId >> 22) % 6);
}

public sealed class GuildUserAvatarObservationWorker(IGuildUserAvatarCacheRepository cache, TimeProvider timeProvider, ILogger<GuildUserAvatarObservationWorker> logger) : BackgroundService, IGuildUserAvatarObserver
{
    private readonly Channel<ObservedGuildUserAvatar> channel = Channel.CreateBounded<ObservedGuildUserAvatar>(new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), string> fingerprints = new();

    public void Observe(SocketGuildUser user)
    {
        var observed = GuildUserAvatarObservation.Create(user, timeProvider.GetUtcNow().UtcDateTime);
        if (!channel.Writer.TryWrite(observed)) logger.LogDebug("Avatar observation queue is full; dropped user {UserId} in guild {GuildId}", observed.UserId, observed.GuildId);
    }

    public override Task StopAsync(CancellationToken cancellationToken) { channel.Writer.TryComplete(); return base.StopAsync(cancellationToken); }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var first in channel.Reader.ReadAllAsync(stoppingToken))
        {
            var pending = new Dictionary<(ulong, ulong), ObservedGuildUserAvatar> { [(first.GuildId, first.UserId)] = first };
            while (channel.Reader.TryRead(out var next)) pending[(next.GuildId, next.UserId)] = next;
            foreach (var group in pending.Values.GroupBy(x => x.GuildId))
            {
                try
                {
                    var observations = group.ToArray();
                    var existing = await cache.GetManyAsync(group.Key, observations.Select(x => x.UserId).ToArray(), stoppingToken);
                    var changes = observations.Where(observed => !fingerprints.TryGetValue((observed.GuildId, observed.UserId), out var fingerprint) || fingerprint != Fingerprint(observed))
                        .Where(observed => !existing.TryGetValue(observed.UserId, out var entry) || !GuildUserAvatarObservation.Matches(entry, observed))
                        .Select(observed => GuildUserAvatarObservation.Apply(existing.GetValueOrDefault(observed.UserId), observed, timeProvider.GetUtcNow().UtcDateTime)).ToArray();
                    if (changes.Length == 0) continue;
                    await cache.BulkUpsertAsync(changes, stoppingToken);
                    foreach (var change in changes) fingerprints[(change.GuildId, change.UserId)] = Fingerprint(change);
                }
                catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogError(exception, "Unable to persist buffered Discord avatar observations"); }
            }
        }
    }

    private static string Fingerprint(ObservedGuildUserAvatar entry) => $"{entry.GuildId}:{entry.UserId}:{entry.AvatarId}:{entry.GuildAvatarId}:{entry.DefaultAvatarIndex}";
    private static string Fingerprint(GuildUserAvatarCacheEntry entry) => $"{entry.GuildId}:{entry.UserId}:{entry.AvatarId}:{entry.GuildAvatarId}:{entry.DefaultAvatarIndex}";
}
