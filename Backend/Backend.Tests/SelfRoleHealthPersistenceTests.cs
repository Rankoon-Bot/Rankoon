using Discord;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Backend.Tests;
using Xunit;

namespace Backend.Tests;

public sealed class SelfRoleHealthPersistenceTests : IAsyncLifetime
{
    private readonly string databaseName = $"rankoon_self_role_health_{Guid.NewGuid():N}";
    private MongoClient client = null!;
    private RankoonDbContext database = null!;
    private readonly CheckStub checker = new();
    private SelfRoleHealthService health = null!;

    public Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("RANKOON_SEASON_TEST_MONGO")!;
        client = new MongoClient(connection);
        database = new RankoonDbContext(Options.Create(new MongoDbSettings { ConnectionString = connection, DatabaseName = databaseName }));
        health = new SelfRoleHealthService(database, checker, new OfflineContext(), TimeProvider.System);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => client.DropDatabaseAsync(databaseName);

    [SeasonMongoFact]
    public async Task Degraded_panels_are_rechecked_and_success_clears_stale_errors_without_republishing()
    {
        var panel = await InsertPanel();
        await health.RefreshAsync(1);
        var saved = await Load(panel.Id!);

        Assert.Equal(SelfRolePanelState.Published, saved.State);
        Assert.Equal("Published", saved.Status);
        Assert.Null(saved.LastError);
        Assert.Null(saved.LastErrorAt);
        Assert.NotNull(saved.LastHealthCheckAt);
        Assert.Equal(panel.LastPublishedAt, saved.LastPublishedAt);
        Assert.Equal(panel.Revision, saved.Revision);
        Assert.Equal(panel.UpdatedAt, saved.UpdatedAt);
        Assert.Equal(panel.MessageId, saved.MessageId);
    }

    [SeasonMongoFact]
    public async Task Page_refresh_is_throttled_and_reconnection_rechecks_while_disabled_panels_are_ignored()
    {
        await InsertPanel();
        var disabled = await InsertPanel(enabled: false);
        await health.RefreshAsync(1);
        await health.RefreshAsync(1);
        Assert.Equal(1, checker.Calls);

        await health.RefreshAsync(1, force: true, restoreMissingReactions: true);
        Assert.Equal(2, checker.Calls);
        Assert.True(checker.RestoreMissingReactions);
        Assert.Equal(SelfRolePanelState.Disabled, (await Load(disabled.Id!)).State);
    }

    [SeasonMongoFact]
    public async Task Inconclusive_failure_can_recover_on_the_next_check()
    {
        var panel = await InsertPanel();
        checker.Result = SelfRoleHealthResult.Unknown();
        await health.RefreshAsync(1);
        Assert.Equal(SelfRolePanelState.Unknown, (await Load(panel.Id!)).State);

        checker.Result = SelfRoleHealthResult.Healthy;
        await health.RefreshAsync(1, force: true);
        Assert.Equal(SelfRolePanelState.Published, (await Load(panel.Id!)).State);
    }

    [SeasonMongoFact]
    public async Task Check_cannot_overwrite_a_concurrent_edit_or_disable()
    {
        var panel = await InsertPanel();
        checker.DuringCheck = () => database.SelfRolePanels.UpdateOneAsync(current => current.Id == panel.Id,
            Builders<SelfRolePanel>.Update.Inc(current => current.Revision, 1)
                .Set(current => current.Enabled, false).Set(current => current.State, SelfRolePanelState.Disabled));

        await health.RefreshAsync(1);

        var saved = await Load(panel.Id!);
        Assert.False(saved.Enabled);
        Assert.Equal(SelfRolePanelState.Disabled, saved.State);
        Assert.Null(saved.LastHealthCheckAt);
    }

    [SeasonMongoFact]
    public async Task Check_cannot_overwrite_a_newer_observation_of_the_same_revision()
    {
        var panel = await InsertPanel();
        checker.DuringCheck = () => database.SelfRolePanels.UpdateOneAsync(current => current.Id == panel.Id,
            Builders<SelfRolePanel>.Update.Set(current => current.LastHealthCheckAt, DateTime.UtcNow)
                .Set(current => current.LastError, "newer observation"));

        await health.RefreshAsync(1);
        Assert.Equal("newer observation", (await Load(panel.Id!)).LastError);
    }

    private async Task<SelfRolePanel> InsertPanel(bool enabled = true)
    {
        var panel = new SelfRolePanel
        {
            GuildId = 1, MessageId = 2, ChannelId = 3, Enabled = enabled,
            State = enabled ? SelfRolePanelState.Degraded : SelfRolePanelState.Disabled,
            LastError = "selfRoles.publishFailed", LastErrorAt = DateTime.UnixEpoch,
            LastPublishedAt = DateTime.UnixEpoch, UpdatedAt = DateTime.UnixEpoch
        };
        await database.SelfRolePanels.InsertOneAsync(panel);
        return panel;
    }

    private Task<SelfRolePanel> Load(string id) => database.SelfRolePanels.Find(panel => panel.Id == id).SingleAsync();

    private sealed class CheckStub : ISelfRoleHealthChecker
    {
        public int Calls { get; private set; }
        public bool RestoreMissingReactions { get; private set; }
        public SelfRoleHealthResult Result { get; set; } = SelfRoleHealthResult.Healthy;
        public Func<Task>? DuringCheck { get; set; }
        public async Task<SelfRoleHealthResult> CheckAsync(IDiscordClient? discord, SelfRolePanel panel, bool restoreMissingReactions, CancellationToken cancellationToken)
        {
            Calls++;
            RestoreMissingReactions = restoreMissingReactions;
            if (DuringCheck != null) await DuringCheck();
            return Result;
        }
    }

    private sealed class OfflineContext : IGuildDiscordContextResolver
    {
        public ValueTask<GuildDiscordContext?> ResolveAsync(ulong guildId, CancellationToken cancellationToken = default) => ValueTask.FromResult<GuildDiscordContext?>(null);
        public Task<IReadOnlyDictionary<ulong, GuildDiscordContext>> ResolveManyAsync(IEnumerable<ulong> guildIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
