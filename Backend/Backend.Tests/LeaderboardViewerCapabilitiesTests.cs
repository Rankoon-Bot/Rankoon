using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Discord;
using Rankoon.Data.Auth;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class LeaderboardViewerCapabilitiesTests
{
    [Theory]
    [InlineData(typeof(LeaderboardPageDto))]
    [InlineData(typeof(LeaderboardWindowDto))]
    public void Leaderboard_responses_expose_nullable_viewer_capabilities(Type responseType)
    {
        var property = responseType.GetProperty(nameof(LeaderboardPageDto.ViewerCapabilities));

        Assert.NotNull(property);
        Assert.Equal(typeof(LeaderboardViewerCapabilitiesDto), property.PropertyType);
        Assert.Equal(NullabilityState.Nullable, new NullabilityInfoContext().Create(property).ReadState);
    }

    [Fact]
    public void Capability_contract_uses_xp_module_names()
    {
        var json = JsonSerializer.Serialize(new LeaderboardViewerCapabilitiesDto("42", true, false), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("{\"guildId\":\"42\",\"canAuditXp\":true,\"canAdjustXp\":false}", json);
    }

    [Fact]
    public async Task Anonymous_viewers_receive_null_without_an_authorization_lookup()
    {
        var authorization = new StubGuildAuthorizationService([GuildModuleIds.XpAudit]);

        var capabilities = await LeaderboardViewerCapabilitiesResolver.ResolveAsync(authorization, new ClaimsPrincipal(new ClaimsIdentity()), 42);

        Assert.Null(capabilities);
        Assert.Equal(0, authorization.AccessibleModuleLookups);
    }

    [Theory]
    [InlineData(new string[0], null, null)]
    [InlineData(new[] { GuildModuleIds.XpAudit }, true, false)]
    [InlineData(new[] { GuildModuleIds.XpAudit, GuildModuleIds.XpAdjustments }, true, true)]
    public async Task Authenticated_viewer_capabilities_follow_existing_module_authorization(string[] moduleIds, bool? expectedAudit, bool? expectedAdjustments)
    {
        var authorization = new StubGuildAuthorizationService(moduleIds);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("discord_id", "123")], "test"));

        var capabilities = await LeaderboardViewerCapabilitiesResolver.ResolveAsync(authorization, user, 42);

        Assert.Equal(expectedAudit, capabilities?.CanAuditXp);
        Assert.Equal(expectedAdjustments, capabilities?.CanAdjustXp);
        if (capabilities != null) Assert.Equal("42", capabilities.GuildId);
        Assert.Equal(1, authorization.AccessibleModuleLookups);
        Assert.Same(user, authorization.LastUser);
        Assert.Equal(42UL, authorization.LastGuildId);
    }

    private sealed class StubGuildAuthorizationService(IReadOnlyList<string> moduleIds) : IGuildAuthorizationService
    {
        public int AccessibleModuleLookups { get; private set; }
        public ClaimsPrincipal? LastUser { get; private set; }
        public ulong LastGuildId { get; private set; }

        public Task<IReadOnlyList<string>> GetAccessibleModuleIdsAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default)
        {
            AccessibleModuleLookups++;
            LastUser = user;
            LastGuildId = guildId;
            return Task.FromResult(moduleIds);
        }

        public Task<IGuildUser?> ResolveMemberAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsOwnerAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsMemberAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> CanAccessAnyModuleAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> CanAccessModuleAsync(ClaimsPrincipal user, ulong guildId, string moduleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ulong? GetDiscordUserId(ClaimsPrincipal user) => throw new NotSupportedException();
    }
}
