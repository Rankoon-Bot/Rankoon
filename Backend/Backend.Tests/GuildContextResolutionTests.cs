using Discord.WebSocket;
using Rankoon.Data.Discord;
using Xunit;

namespace Backend.Tests;

public sealed class GuildContextResolutionTests
{
    [Fact]
    public async Task ResolveManyAsync_deduplicates_guild_ids()
    {
        var runtimes = new NullRuntimeManager();
        var resolver = new GuildDiscordContextResolver(runtimes);

        var contexts = await resolver.ResolveManyAsync([1, 1, 2]);

        Assert.Empty(contexts);
        Assert.Equal([1UL, 2UL], runtimes.ResolvedGuildIds);
    }

    private sealed class NullRuntimeManager : IBotRuntimeManager
    {
        public List<ulong> ResolvedGuildIds { get; } = [];
        public IReadOnlyCollection<BotRuntimeSnapshot> GetRuntimeSnapshots() => [];
        public CustomBotRuntimeStatus GetCustomRuntimeStatus() => new(0, 0, 0, 1, 1, false);
        public ValueTask<BotRuntimeContext?> ResolveGuildAsync(ulong guildId, CancellationToken cancellationToken = default)
        {
            ResolvedGuildIds.Add(guildId);
            return ValueTask.FromResult<BotRuntimeContext?>(null);
        }
        public ValueTask<BotRuntimeContext?> GetPlatformRuntimeAsync(ulong guildId, CancellationToken cancellationToken = default) => ValueTask.FromResult<BotRuntimeContext?>(null);
        public ValueTask<BotRuntimeContext?> GetCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default) => ValueTask.FromResult<BotRuntimeContext?>(null);
        public Task<CustomBotRuntimeStartResult> StartCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAllCustomRuntimesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CustomBotRuntimeStartResult> RestartCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
