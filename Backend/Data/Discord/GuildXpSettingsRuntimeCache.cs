using System.Collections.Concurrent;
using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Xp;

namespace Rankoon.Data.Discord;

// This is intentionally process-local. A distributed invalidation bus is required when API instances are scaled out.
public sealed record GuildXpSettingsSnapshot(ulong GuildId, bool Enabled, VoiceXpSettings Voice, IReadOnlySet<ulong> ExcludedChannelIds, IReadOnlySet<ulong> ExcludedCategoryIds, IReadOnlySet<ulong> ExcludedRoleIds, IReadOnlyDictionary<ulong, decimal> ChannelMultipliers, ServerBoosterXpSettings ServerBooster, long Revision, DateTime UpdatedAtUtc);
public sealed record GuildXpSettingsChanged(ulong GuildId, long Revision, GuildXpSettingsSnapshot Snapshot, DateTime ChangedAtUtc);

public interface IGuildXpSettingsRuntimeCache
{
    bool TryGet(ulong guildId, out GuildXpSettingsSnapshot settings);
    GuildXpSettingsSnapshot? GetOrDefault(ulong guildId);
    void Apply(GuildXpSettingsSnapshot settings);
    void Remove(ulong guildId);
    IReadOnlyCollection<ulong> GetVoiceEnabledGuildIds();
    Task LoadAsync(CancellationToken cancellationToken = default);
}
public interface IGuildXpSettingsChangePublisher { ValueTask PublishAsync(GuildXpSettingsChanged change, CancellationToken cancellationToken = default); }
public interface IGuildXpSettingsChangeConsumer { ValueTask HandleAsync(GuildXpSettingsChanged change, CancellationToken cancellationToken = default); }

public sealed class GuildXpSettingsRuntimeCache(RankoonDbContext database) : IGuildXpSettingsRuntimeCache, IGuildXpSettingsChangeConsumer
{
    private readonly ConcurrentDictionary<ulong, GuildXpSettingsSnapshot> entries = new();
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private volatile bool initialized;
    public bool TryGet(ulong guildId, out GuildXpSettingsSnapshot settings) => entries.TryGetValue(guildId, out settings!);
    public GuildXpSettingsSnapshot? GetOrDefault(ulong guildId) => entries.TryGetValue(guildId, out var settings) ? settings : null;
    public void Apply(GuildXpSettingsSnapshot settings) => entries.AddOrUpdate(settings.GuildId, settings, (_, current) => settings.Revision > current.Revision ? settings : current);
    public void Remove(ulong guildId) => entries.TryRemove(guildId, out _);
    public IReadOnlyCollection<ulong> GetVoiceEnabledGuildIds() => entries.Values.Where(x => x.Enabled && x.Voice.Enabled).Select(x => x.GuildId).ToArray();
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (initialized) return;
        await initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (initialized) return;
            var settings = await database.GuildXpSettings.Find(Builders<GuildXpSettings>.Filter.Empty).ToListAsync(cancellationToken);
            foreach (var setting in settings) Apply(CreateSnapshot(setting));
            initialized = true;
        }
        finally { initializationGate.Release(); }
    }
    public ValueTask HandleAsync(GuildXpSettingsChanged change, CancellationToken cancellationToken = default) { Apply(change.Snapshot); return ValueTask.CompletedTask; }
    public static GuildXpSettingsSnapshot CreateSnapshot(GuildXpSettings settings)
    {
        var voice = VoiceXpSettingsNormalizer.CloneNormalized(settings.Voice); var booster = settings.ServerBooster ?? new ServerBoosterXpSettings();
        return new(settings.GuildId, settings.Enabled, voice,
            (settings.ExcludedChannelIds ?? []).ToHashSet(), (settings.ExcludedCategoryIds ?? []).ToHashSet(), (settings.ExcludedRoleIds ?? []).ToHashSet(), (settings.ChannelMultipliers ?? []).GroupBy(x => x.ChannelId).ToDictionary(x => x.Key, x => x.Last().Multiplier),
            new ServerBoosterXpSettings { Enabled = booster.Enabled, Tiers = (booster.Tiers ?? []).Select(x => new ServerBoosterXpTier { MinimumBoostMonths = x.MinimumBoostMonths, Multiplier = x.Multiplier }).ToList() }, settings.Revision, settings.UpdatedAt);
    }
}
public sealed class InProcessGuildXpSettingsChangePublisher(IServiceProvider services) : IGuildXpSettingsChangePublisher
{
    public async ValueTask PublishAsync(GuildXpSettingsChanged change, CancellationToken cancellationToken = default)
    { foreach (var consumer in services.GetServices<IGuildXpSettingsChangeConsumer>()) await consumer.HandleAsync(change, cancellationToken); }
}
