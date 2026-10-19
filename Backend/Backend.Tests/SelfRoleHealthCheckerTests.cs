using Discord;
using Discord.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using System.Net;
using Xunit;
using Stub = Backend.Tests.LevelRoleReliabilityTests.InterfaceStub;

namespace Backend.Tests;

public sealed class SelfRoleHealthCheckerTests
{
    [Fact]
    public async Task Working_panel_recovers_without_edit_permissions_or_readding_existing_reactions()
    {
        var discord = new PanelDiscord();
        discord.Panel.State = SelfRolePanelState.Degraded;
        discord.Panel.LastError = "selfRoles.publishFailed";

        Assert.Equal(SelfRoleHealthResult.Healthy, await discord.Check(restore: true));
        Assert.Equal(CacheMode.AllowDownload, discord.MessageCacheMode);
        Assert.Equal(0, discord.AddReactionCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Inconclusive_discord_errors_do_not_claim_the_panel_is_broken(HttpStatusCode status)
    {
        var discord = new PanelDiscord { MessageError = new HttpException(status, null!) };
        Assert.Equal(SelfRolePanelState.Unknown, (await discord.Check()).State);
    }

    [Fact]
    public async Task Timeout_is_inconclusive_and_a_later_success_recovers()
    {
        var discord = new PanelDiscord { MessageError = new TaskCanceledException() };
        Assert.Equal(SelfRolePanelState.Unknown, (await discord.Check()).State);
        discord.MessageError = null;
        Assert.Equal(SelfRoleHealthResult.Healthy, await discord.Check());
    }

    [Fact]
    public async Task Confirmed_deleted_message_requires_repair()
    {
        var discord = new PanelDiscord { MessageError = new HttpException(HttpStatusCode.NotFound, null!, DiscordErrorCode.UnknownMessage) };
        Assert.Equal(SelfRoleHealthResult.Degraded("messageMissing"), await discord.Check());
    }

    [Fact]
    public async Task Confirmed_missing_channel_requires_attention_and_offline_runtime_is_inconclusive()
    {
        var discord = new PanelDiscord { ChannelAvailable = false };
        Assert.Equal(SelfRoleHealthResult.Degraded("channelMissing"), await discord.Check());
        Assert.Equal(SelfRolePanelState.Unknown, (await new SelfRoleHealthChecker(NullLogger<SelfRoleHealthChecker>.Instance)
            .CheckAsync(null, discord.Panel, false, default)).State);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 10)]
    public async Task Missing_role_permission_or_role_above_bot_requires_attention(bool manageRoles, int rolePosition)
    {
        var discord = new PanelDiscord { ManageRoles = manageRoles, RolePosition = rolePosition };
        Assert.Equal(SelfRoleHealthResult.Degraded("rolePermissions"), await discord.Check());
    }

    [Fact]
    public async Task Failed_saved_edit_is_not_cleared_by_an_older_working_message()
    {
        var discord = new PanelDiscord();
        discord.Panel.Embeds[0].Title = "Unpublished change";
        Assert.Equal(SelfRoleHealthResult.Degraded("outOfSync"), await discord.Check());
    }

    [Fact]
    public async Task Dashboard_check_is_read_only_and_reconnection_restores_only_missing_reactions()
    {
        var discord = new PanelDiscord();
        discord.Reactions.Clear();
        Assert.Equal(SelfRoleHealthResult.Degraded("reactionsMissing"), await discord.Check());
        Assert.Equal(0, discord.AddReactionCalls);
        Assert.Equal(SelfRoleHealthResult.Healthy, await discord.Check(restore: true));
        Assert.Equal(1, discord.AddReactionCalls);
    }

    [Fact]
    public async Task Cancellation_is_not_saved_as_a_panel_failure()
    {
        var discord = new PanelDiscord();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discord.Check(cancellationToken: cancellation.Token));
    }

    private sealed class PanelDiscord
    {
        public SelfRolePanel Panel { get; } = new()
        {
            GuildId = 1, ChannelId = 2, MessageId = 3,
            Mappings = [new() { RoleId = 42, Emoji = new() { Value = "✅" } }],
            Embeds = [new() { Kind = SelfRoleEmbedKind.RoleMappings, Title = "Roles", Fields = [new() { Name = "Choice", Value = "Select a role", Inline = true }] }]
        };
        public Exception? MessageError { get; set; }
        public bool ChannelAvailable { get; set; } = true;
        public bool ManageRoles { get; set; } = true;
        public int RolePosition { get; set; } = 1;
        public int AddReactionCalls { get; private set; }
        public CacheMode? MessageCacheMode { get; private set; }
        public Dictionary<IEmote, ReactionMetadata> Reactions { get; } = new()
        {
            [new Emoji("✅")] = Reaction()
        };
        private readonly IDiscordClient client;

        public PanelDiscord()
        {
            var embeds = SelfRoleMessageRenderer.BuildEmbeds(Panel).Cast<IEmbed>().ToArray();
            var message = Stub.Create<IUserMessage>((method, _) => method.Name switch
            {
                "get_Embeds" => embeds,
                "get_Reactions" => Reactions,
                "AddReactionAsync" => AddReaction(),
                _ => throw new NotSupportedException(method.Name)
            });
            var channel = Stub.Create<ITextChannel>((method, args) =>
            {
                if (method.Name != "GetMessageAsync") throw new NotSupportedException(method.Name);
                MessageCacheMode = (CacheMode)args![1]!;
                return MessageError == null ? Task.FromResult<IMessage>(message) : Task.FromException<IMessage>(MessageError);
            });
            var bot = Stub.Create<IGuildUser>((method, _) => method.Name switch
            {
                "get_GuildPermissions" => new GuildPermissions(manageRoles: ManageRoles),
                "get_Hierarchy" => 10,
                "GetPermissions" => new ChannelPermissions(viewChannel: true),
                _ => throw new NotSupportedException(method.Name)
            });
            var role = Stub.Create<IRole>((method, _) => method.Name switch
            {
                "get_Id" => 42UL,
                "get_IsManaged" => false,
                "get_Position" => RolePosition,
                _ => throw new NotSupportedException(method.Name)
            });
            var guild = Stub.Create<IGuild>((method, _) => method.Name switch
            {
                "get_Id" => 1UL,
                "GetTextChannelAsync" => Task.FromResult(ChannelAvailable ? channel : null),
                "GetCurrentUserAsync" => Task.FromResult(bot),
                "GetRole" => role,
                _ => throw new NotSupportedException(method.Name)
            });
            client = Stub.Create<IDiscordClient>((method, _) => method.Name == "GetGuildAsync"
                ? Task.FromResult(guild) : throw new NotSupportedException(method.Name));
        }

        private Task AddReaction() { AddReactionCalls++; return Task.CompletedTask; }
        private static ReactionMetadata Reaction()
        {
            object metadata = new ReactionMetadata();
            typeof(ReactionMetadata).GetProperty(nameof(ReactionMetadata.ReactionCount))!.SetValue(metadata, 1);
            return (ReactionMetadata)metadata;
        }
        public Task<SelfRoleHealthResult> Check(bool restore = false, CancellationToken cancellationToken = default) =>
            new SelfRoleHealthChecker(NullLogger<SelfRoleHealthChecker>.Instance).CheckAsync(client, Panel, restore, cancellationToken);
    }
}
